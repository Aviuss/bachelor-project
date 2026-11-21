import sys
import dt4dds
from pathlib import Path
import json

INPUT_DIR = Path("/data/input")
OUTPUT_DIR = Path("/data/output")
CONFIG_FILE = INPUT_DIR / "config.json"
SEQUENCES_FILE = INPUT_DIR / "sequences.txt"


# ---------------------------------------------------------------------------
# Setup
# ---------------------------------------------------------------------------
def design_primers():
    primers_0 = ["ACACGACGCTCTTCCGATCT", "AGACGTGTGCTCTTCCGATCT"]
    primers_2 = ["AATGATACGGCGACCACCGAGATCTACACTCTTTCCCTACACGACGCTCTTCCGATCT", "CAAGCAGAAGACGGCATACGAGATCGTGATGTGACTGGAGTTCAGACGTGTGCTCTTCCGATCT"]
    return primers_0, primers_2


def basic_properties():
    dt4dds.properties.set_property_settings(
        dt4dds.settings.defaults.SequenceProperties(
            efficiency_distribution='normal',
            efficiency_params={'loc': 1.0, 'scale': 0.0051},
        )
    )


# ---------------------------------------------------------------------------
# Synthesis
# ---------------------------------------------------------------------------
def perfect_synthesis(seq_list, n_seqs, primers_0):
    # defaults for electrochemical synthesis, with all error rates set to zero
    synthesis_settings = dt4dds.settings.defaults.ArraySynthesis_Twist()
    array_synthesis = dt4dds.processes.ArraySynthesis(synthesis_settings(
        oligo_distribution_type='lognormal',
        oligo_distribution_params={'mean': 0, 'sigma': 0.30},
        deletion_rate=0,
        insertion_rate=0,
        substitution_rate=0
    ))
    array_synthesis.process(seq_list)

    # sample with mean coverage of 1000
    pool = array_synthesis.sample_by_counts(1000 * n_seqs)
    pool = dt4dds.generators.attach_primers_to_pool(pool, *primers_0)
    pool.volume = 10
    return pool


def real_synthesis(seq_list, n_seqs, primers_0):
    # defaults for electrochemical synthesis
    synthesis_settings = dt4dds.settings.defaults.ArraySynthesis_Twist()
    array_synthesis = dt4dds.processes.ArraySynthesis(synthesis_settings(
        oligo_distribution_type='lognormal',
        oligo_distribution_params={'mean': 0, 'sigma': 0.30},
    ))
    array_synthesis.process(seq_list)

    # sample with mean coverage of 2000
    pool = array_synthesis.sample_by_counts(2000 * n_seqs)
    pool = dt4dds.generators.attach_primers_to_pool(pool, *primers_0)
    pool.volume = 10
    return pool


def synthesis(config, seq_list, n_seqs, primers_0):
    if config["processes"]["synthesis"]["is_perfect"]:
        return perfect_synthesis(seq_list, n_seqs, primers_0)
    return real_synthesis(seq_list, n_seqs, primers_0)


# ---------------------------------------------------------------------------
# PCR
# ---------------------------------------------------------------------------
def perfect_pcr(pool, primers_2):
    pcr_settings = dt4dds.settings.defaults.PCR_HiFi()
    pcr_process = dt4dds.processes.PCR(
        pcr_settings(
            primers=primers_2,
            template_volume=1,
            volume=20,
            efficiency_mean=1,
            n_cycles=20,
            polymerase_basesubstitutionrate=0,
            polymerase_fidelity=100
        )
    )
    return pcr_process.process(pool)


def real_pcr(pool, primers_2, n_cycles):
    pcr_process = dt4dds.processes.PCR(primers=primers_2, n_cycles=n_cycles)
    return pcr_process.process(pool)


def pcr(config, pool, primers_2):
    n_cycles = config["processes"]["pcr"]["n_cycles"]

    if config["processes"]["pcr"]["is_perfect"]:
        return perfect_pcr(pool, primers_2)
    return real_pcr(pool, primers_2, n_cycles)


# ---------------------------------------------------------------------------
# Aging
# ---------------------------------------------------------------------------
def real_aging(pool):
    aging_settings = dt4dds.settings.defaults.Aging()
    aging_process = dt4dds.processes.Aging(
        aging_settings(),
        n_halflives=2,
    )
    pool = aging_process.process(pool)
    pool.volume = 5
    return pool


def aging(config, pool):
    # perfect aging = no aging
    if not config["processes"]["aging"]["is_perfect"]:
        return real_aging(pool)
    return pool


# ---------------------------------------------------------------------------
# Dilution (a single dilution step, right before sequencing)
# ---------------------------------------------------------------------------
# Optional section in config.json (no section = no dilution):
#
#   "processes": {
#       ...
#       "dilution": {"method": "molar_concentration", "value": 0.02, "final_volume": 20}
#   }
#
#   method        - counts [molecules] | moles [nmol] | mass [ng] | volume [uL] |
#                   factor (0 < x < 1) | molar_concentration [nM] | mass_concentration [ng/uL]
#   value         - target value in the unit of the chosen method (> 0)
#   final_volume  - volume after dilution [uL]; required for the *_concentration methods
#                   and recommended always (without it the pool keeps the volume of the
#                   sampled portion, which may be a fraction of a uL)
#
# nM is real nanomolar (1e-9 mol/L; 20 pM = 0.02). Do not compare it with
# pool.molar_concentration from dt4dds, which returns a value 1e6 times smaller.
SAMPLERS = {
    "counts":              lambda pool, v, fv: pool.sample_by_counts(v, accurate=True),
    "moles":               lambda pool, v, fv: pool.sample_by_moles(v, accurate=True),
    "mass":                lambda pool, v, fv: pool.sample_by_mass(v, accurate=True),
    "volume":              lambda pool, v, fv: pool.sample_by_volume(v, accurate=True),
    "factor":              lambda pool, v, fv: pool.sample_by_factor(v, accurate=True),
    "molar_concentration": lambda pool, v, fv: pool.sample_by_moles(v * fv * 1e-6, accurate=True),  # nM * uL -> nmol
    "mass_concentration":  lambda pool, v, fv: pool.sample_by_mass(v * fv, accurate=True),          # ng/uL * uL -> ng
}


def validate_dilution_config(config):
    """Check the dilution section before the simulation starts, so a typo doesn't surface hours later."""
    cfg = config["processes"].get("dilution")
    if not cfg:
        return

    method, value, final_volume = cfg.get("method"), cfg.get("value"), cfg.get("final_volume")
    if method not in SAMPLERS:
        raise ValueError(f"dilution: unknown method '{method}', allowed: {list(SAMPLERS)}")
    if not isinstance(value, (int, float)) or isinstance(value, bool) or value <= 0:
        raise ValueError(f"dilution: 'value' must be a number > 0, got: {value!r}")
    if method == "factor" and value >= 1:
        raise ValueError(f"dilution: for 'factor', value must be < 1, got: {value}")
    if method.endswith("_concentration") and final_volume is None:
        raise ValueError(f"dilution: method '{method}' requires 'final_volume'")
    if final_volume is not None and (not isinstance(final_volume, (int, float)) or final_volume <= 0):
        raise ValueError(f"dilution: 'final_volume' must be a number > 0, got: {final_volume!r}")


def dilution(config, pool):
    cfg = config["processes"].get("dilution")
    if not cfg:
        return pool

    n_before = pool.n_oligos
    final_volume = cfg.get("final_volume")

    diluted = SAMPLERS[cfg["method"]](pool, cfg["value"], final_volume)
    if final_volume is not None:
        diluted.volume = final_volume  # top up with diluent

    print(f"[dilution] {cfg['method']}={cfg['value']}: "
          f"{n_before} -> {diluted.n_oligos} molecules, volume {diluted.volume} uL")
    return diluted


# ---------------------------------------------------------------------------
# Sequencing-by-synthesis
# ---------------------------------------------------------------------------
def perfect_sequencing(pool):
    # perfect sequencing = SBS with the substitution rate set to zero
    # (R1.fq.gz and R2.fq.gz are saved to OUTPUT_DIR)
    sbs_sequencing = dt4dds.processes.SBSSequencing(
        output_directory=OUTPUT_DIR,
        n_reads=1000000,
        read_length=150,
        read_mode="paired-end",
        primers_read=[
            "ACACTCTTTCCCTACACGACGCTCTTCCGATCT",
            "GTGACTGGAGTTCAGACGTGTGCTCTTCCGATCT",
        ],
        substitution_rates=[0, 0],   # [R1, R2]
    )
    sbs_sequencing.process(pool)


def real_sequencing(pool):
    # sequencing data is saved to the output directory
    sbs_sequencing = dt4dds.processes.SBSSequencing(output_directory=OUTPUT_DIR)
    sbs_sequencing.process(pool)


def sequencing(config, pool):
    if config["processes"]["sequencing"]["is_perfect"]:
        perfect_sequencing(pool)
    else:
        real_sequencing(pool)


# ---------------------------------------------------------------------------
# Main workflow
# ---------------------------------------------------------------------------
def main():
    if not CONFIG_FILE.exists():
        print("No config file")
        sys.exit(1)
    with open(CONFIG_FILE, 'r', encoding='utf-8') as f:
        config = json.load(f)

    print("Your settings:", config)
    validate_dilution_config(config)
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)

    basic_properties()
    primers_0, primers_2 = design_primers()
    seq_list = dt4dds.tools.txt_to_seqlist(SEQUENCES_FILE)
    n_seqs = len(seq_list)

    pool = synthesis(config, seq_list, n_seqs, primers_0)
    pool = pcr(config, pool, primers_2)
    pool = aging(config, pool)
    pool = dilution(config, pool)
    sequencing(config, pool)


if __name__ == "__main__":
    main()