import gzip
import json
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

import dt4dds

INPUT_DIR = Path("/data/input")
OUTPUT_DIR = Path("/data/output")
CONFIG_FILE = INPUT_DIR / "config.json"
FASTA_FILE = INPUT_DIR / "encoded.fasta"
SEQUENCES_FILE = INPUT_DIR / "sequences.txt"

READ_LENGTH = 150
THREADS = 4

OUTPUT_FORMATS = ("fastq", "fasta", "txt")


# ---------------------------------------------------------------------------
# Setup
# ---------------------------------------------------------------------------
def design_primers():
    primers_0 = ["ACACGACGCTCTTCCGATCT", "AGACGTGTGCTCTTCCGATCT"]
    primers_2 = ["AATGATACGGCGACCACCGAGATCTACACTCTTTCCCTACACGACGCTCTTCCGATCT",
                 "CAAGCAGAAGACGGCATACGAGATCGTGATGTGACTGGAGTTCAGACGTGTGCTCTTCCGATCT"]
    return primers_0, primers_2


def basic_properties():
    dt4dds.properties.set_property_settings(
        dt4dds.settings.defaults.SequenceProperties(
            efficiency_distribution='normal',
            efficiency_params={'loc': 1.0, 'scale': 0.0051},
        )
    )


def fasta_to_txt(input_path, output_path):
    """FASTA-like file (">" headers, possibly multi-line sequences) -> one sequence per line."""
    with open(input_path, "r", encoding="utf-8") as src, \
         open(output_path, "w", encoding="utf-8") as dst:
        current = []
        for line in src:
            line = line.strip()
            if line.startswith(">"):
                if current:
                    dst.write("".join(current) + "\n")
                    current = []
            elif line:
                current.append(line)
        if current:
            dst.write("".join(current) + "\n")


def oligo_min_length(sequences_file):
    with open(sequences_file, "r", encoding="utf-8") as f:
        lengths = [len(line.strip()) for line in f if line.strip()]
    if not lengths:
        raise ValueError(f"{sequences_file} contains no sequences")
    return min(lengths)


# ---------------------------------------------------------------------------
# Config validation (everything before the long simulation starts)
# ---------------------------------------------------------------------------
SAMPLERS = {
    "counts":              lambda pool, v, fv: pool.sample_by_counts(v, accurate=True),
    "moles":               lambda pool, v, fv: pool.sample_by_moles(v, accurate=True),
    "mass":                lambda pool, v, fv: pool.sample_by_mass(v, accurate=True),
    "volume":              lambda pool, v, fv: pool.sample_by_volume(v, accurate=True),
    "factor":              lambda pool, v, fv: pool.sample_by_factor(v, accurate=True),
    "molar_concentration": lambda pool, v, fv: pool.sample_by_moles(v * fv * 1e-6, accurate=True),  # nM * uL -> nmol
    "mass_concentration":  lambda pool, v, fv: pool.sample_by_mass(v * fv, accurate=True),          # ng/uL * uL -> ng
}


def validate_config(config):
    if config.get("output_format", "fastq") not in OUTPUT_FORMATS:
        raise ValueError(f"output_format: {config['output_format']!r}, allowed: {list(OUTPUT_FORMATS)}")
    for key in ("output_merge", "output_dereplication"):
        if key in config and not isinstance(config[key], bool):
            raise ValueError(f"{key}: must be true or false, got: {config[key]!r}")

    if shutil.which("fastp") is None:
        raise RuntimeError("'fastp' not found in PATH (conda install -c bioconda fastp)")

    cfg = config["processes"].get("dilution")
    if cfg:
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


# ---------------------------------------------------------------------------
# Synthesis
# config: method (only "Array"), oligo_distribution_type, oligo_distribution_params,
#         sampling (mean coverage), volume
# ---------------------------------------------------------------------------
def synthesis(config, seq_list, n_seqs, primers_0):
    cfg = config["processes"]["synthesis"]
    is_perfect = cfg["is_perfect"]

    if cfg.get("method", "Array") != "Array":
        raise ValueError(f"synthesis: unsupported method {cfg['method']!r}, only 'Array'")

    overrides = dict(
        oligo_distribution_type=cfg.get("oligo_distribution_type", "lognormal"),
        oligo_distribution_params=cfg.get("oligo_distribution_params", {"mean": 0, "sigma": 0.30}),
    )
    if is_perfect:
        overrides.update(deletion_rate=0, insertion_rate=0, substitution_rate=0)

    settings = dt4dds.settings.defaults.ArraySynthesis_Twist()
    array_synthesis = dt4dds.processes.ArraySynthesis(settings(**overrides))
    array_synthesis.process(seq_list)

    coverage = cfg.get("sampling", 1000 if is_perfect else 2000)
    pool = array_synthesis.sample_by_counts(coverage * n_seqs)
    pool = dt4dds.generators.attach_primers_to_pool(pool, *primers_0)
    pool.volume = cfg.get("volume", 10)
    return pool


# ---------------------------------------------------------------------------
# PCR
# config: pcr_mode ("HiFi" -> dt4dds.settings.defaults.PCR_<mode>), primers
#         ("primers_0" | "primers_2"), template_volume, volume, efficiency_mean, n_cycles
#         polymerase_basesubstitutionrate / polymerase_fidelity: only used when is_perfect = true
# ---------------------------------------------------------------------------
def pcr(config, pool, primers_0, primers_2):
    cfg = config["processes"]["pcr"]

    primer_sets = {"primers_0": primers_0, "primers_2": primers_2}
    primers_name = cfg.get("primers", "primers_2")
    if primers_name not in primer_sets:
        raise ValueError(f"pcr: unknown primers {primers_name!r}, allowed: {list(primer_sets)}")

    overrides = {"primers": primer_sets[primers_name]}
    for key in ("template_volume", "volume", "efficiency_mean", "n_cycles"):
        if key in cfg:
            overrides[key] = cfg[key]

    if cfg["is_perfect"]:
        overrides.setdefault("template_volume", 1)
        overrides.setdefault("volume", 20)
        overrides.setdefault("efficiency_mean", 1)
        overrides.setdefault("n_cycles", 20)
        overrides.update(polymerase_basesubstitutionrate=0, polymerase_fidelity=100)

    mode = cfg.get("pcr_mode", "HiFi")
    settings_cls = getattr(dt4dds.settings.defaults, f"PCR_{mode}", None)
    if settings_cls is None:
        raise ValueError(f"pcr: unknown pcr_mode {mode!r}")

    process = dt4dds.processes.PCR(settings_cls()(**overrides))
    return process.process(pool)


# ---------------------------------------------------------------------------
# Aging
# config: n_halflives
# ---------------------------------------------------------------------------
def aging(config, pool):
    cfg = config["processes"]["aging"]
    if cfg["is_perfect"]:
        return pool
    settings = dt4dds.settings.defaults.Aging()
    process = dt4dds.processes.Aging(settings(), n_halflives=cfg.get("n_halflives", 2))
    pool = process.process(pool)
    pool.volume = 5
    return pool


# ---------------------------------------------------------------------------
# Dilution (optional, single step right before sequencing)
# "dilution": {"method": "molar_concentration", "value": 0.02, "final_volume": 20}
# nM is real nanomolar (1e-9 mol/L); pool.molar_concentration in dt4dds is 1e6x smaller.
# ---------------------------------------------------------------------------
def dilution(config, pool):
    cfg = config["processes"].get("dilution")
    if not cfg:
        return pool
    final_volume = cfg.get("final_volume")
    diluted = SAMPLERS[cfg["method"]](pool, cfg["value"], final_volume)
    if final_volume is not None:
        diluted.volume = final_volume
    return diluted


# ---------------------------------------------------------------------------
# Sequencing-by-synthesis
# ---------------------------------------------------------------------------
def sequencing(config, pool, output_dir):
    if config["processes"]["sequencing"]["is_perfect"]:
        process = dt4dds.processes.SBSSequencing(
            output_directory=output_dir,
            n_reads=1000000,
            read_length=READ_LENGTH,
            read_mode="paired-end",
            primers_read=[
                "ACACTCTTTCCCTACACGACGCTCTTCCGATCT",
                "GTGACTGGAGTTCAGACGTGTGCTCTTCCGATCT",
            ],
            substitution_rates=[0, 0],
        )
    else:
        process = dt4dds.processes.SBSSequencing(output_directory=output_dir)
    process.process(pool)


# ---------------------------------------------------------------------------
# Preprocessing (fastp) -> single FASTQ.gz
#   output_merge = true : adapters trimmed, R1+R2 merged (unmerged pairs are dropped)
#   output_merge = false: only R1, adapters trimmed (R2 is not used)
# ---------------------------------------------------------------------------
def reverse_complement(seq):
    return seq.upper().translate(str.maketrans("ACGTN", "TGCAN"))[::-1]


def run_fastp(cmd):
    result = subprocess.run(cmd, capture_output=True, text=True)
    if result.returncode != 0:
        raise RuntimeError(f"fastp failed:\n{result.stderr}")


def preprocess(merge, primers_2, min_length, raw_dir, work_dir):
    r1, r2 = raw_dir / "R1.fq.gz", raw_dir / "R2.fq.gz"
    if not (r1.exists() and r2.exists()):
        raise FileNotFoundError(
            f"Expected R1.fq.gz and R2.fq.gz in {raw_dir}, found: {sorted(p.name for p in raw_dir.iterdir())}"
        )

    adapter_r1 = reverse_complement(primers_2[1])
    adapter_r2 = reverse_complement(primers_2[0])
    result = work_dir / "result.fastq.gz"

    common = [
        "fastp",
        "--disable_quality_filtering",   # keep noisy reads, the error-correcting code deals with them
        "--length_required", str(min_length),
        "--thread", str(THREADS),
        "--json", str(work_dir / "fastp.json"),
        "--html", str(work_dir / "fastp.html"),
    ]
    if merge:
        cmd = common + [
            "-i", str(r1), "-I", str(r2),
            "--out1", str(work_dir / "unmerged_R1.fastq.gz"),
            "--out2", str(work_dir / "unmerged_R2.fastq.gz"),
            "--adapter_sequence", adapter_r1,
            "--adapter_sequence_r2", adapter_r2,
            "--correction",
            "--merge", "--merged_out", str(result),
        ]
    else:
        cmd = common + [
            "-i", str(r1),
            "--out1", str(result),
            "--adapter_sequence", adapter_r1,
        ]
    run_fastp(cmd)
    return result


# ---------------------------------------------------------------------------
# Output: output.<fastq|fasta|txt>, optional dereplication (by sequence, first read wins)
# ---------------------------------------------------------------------------
def iter_fastq(fastq_gz):
    with gzip.open(fastq_gz, "rt") as src:
        while True:
            header = src.readline()
            if not header:
                break
            seq = src.readline().strip()
            src.readline()
            qual = src.readline().strip()
            yield header[1:].split()[0], seq, qual


def write_output(fastq_gz, path, fmt, dereplicate):
    seen = set()
    n = 0
    with open(path, "w", encoding="utf-8") as dst:
        for read_id, seq, qual in iter_fastq(fastq_gz):
            if dereplicate:
                if seq in seen:
                    continue
                seen.add(seq)
            if fmt == "fastq":
                dst.write(f"@{read_id}\n{seq}\n+\n{qual}\n")
            elif fmt == "fasta":
                dst.write(f">{read_id}\n{seq}\n")
            else:
                dst.write(seq + "\n")
            n += 1
    return n


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------
def main():
    if not CONFIG_FILE.exists():
        print("No config file")
        sys.exit(1)
    with open(CONFIG_FILE, "r", encoding="utf-8") as f:
        config = json.load(f)

    validate_config(config)

    merge = config.get("output_merge", True)
    dereplicate = config.get("output_dereplication", False)
    fmt = config.get("output_format", "fastq")

    basic_properties()
    primers_0, primers_2 = design_primers()

    with tempfile.TemporaryDirectory() as tmp:
        tmp = Path(tmp)

        sequences_file = SEQUENCES_FILE
        if FASTA_FILE.exists():
            sequences_file = tmp / "sequences.txt"
            fasta_to_txt(FASTA_FILE, sequences_file)
        seq_list = dt4dds.tools.txt_to_seqlist(sequences_file)
        min_length = max(30, int(0.7 * oligo_min_length(sequences_file)))

        pool = synthesis(config, seq_list, len(seq_list), primers_0)
        pool = pcr(config, pool, primers_0, primers_2)
        pool = aging(config, pool)
        pool = dilution(config, pool)

        raw_dir = tmp / "raw"
        raw_dir.mkdir()
        sequencing(config, pool, raw_dir)

        fastq_gz = preprocess(merge, primers_2, min_length, raw_dir, tmp)

        OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
        out_path = OUTPUT_DIR / f"output.{fmt}"
        n = write_output(fastq_gz, out_path, fmt, dereplicate)

    print(f"{out_path}: {n} sequences")


if __name__ == "__main__":
    main()