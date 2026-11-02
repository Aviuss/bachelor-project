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
    primers = [primers_0, primers_2]
    return primers


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

    array_synthesis.process(seq_list)
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

    array_synthesis.process(seq_list)
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
# Sequencing-by-synthesis
# ---------------------------------------------------------------------------
def perfect_sequencing(pool):
    print("Perfect")


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
        exit(1)
    with open(CONFIG_FILE, 'r', encoding='utf-8') as f:
        config = json.load(f)

    print("Your settings:", config)

    primers_0, primers_2 = design_primers()
    seq_list = dt4dds.tools.txt_to_seqlist(SEQUENCES_FILE)
    n_seqs = len(seq_list)

    pool = synthesis(config, seq_list, n_seqs, primers_0)
    pool = pcr(config, pool, primers_2)
    pool = aging(config, pool)
    sequencing(config, pool)


if __name__ == "__main__":
    main()