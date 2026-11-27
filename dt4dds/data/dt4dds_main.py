import gzip
import json
import os
import random
import re
import shutil
import statistics
import subprocess
import sys
import tempfile
from pathlib import Path

import numpy as np

import dt4dds

INPUT_DIR = Path("/data/input")
OUTPUT_DIR = Path("/data/output")
CONFIG_FILE = INPUT_DIR / "config.json"
FASTA_FILE = INPUT_DIR / "encoded.fasta"
SEQUENCES_FILE = INPUT_DIR / "sequences.txt"

READ_LENGTH = 150
THREADS = int(os.environ.get("PIPELINE_THREADS", 4))   # set to 1 when many containers run in parallel

# Values that must be IDENTICAL in every coalition (ideal/real), unless the config says otherwise.
DEFAULT_SAMPLING = 1000      # mean coverage of the common reference pool after synthesis
DEFAULT_N_READS = 100000     # requested reads (a pool with fewer molecules gives fewer reads)
AGING_VOLUME = 5

OUTPUT_FORMATS = ("fastq", "fasta", "txt")
CLUSTER_STRANDS = ("plus", "both")

# every stage gets its own random stream: (seed, stage) -> the same stage behaves identically
# in all coalitions that share the seed, no matter which other stages are ideal or real
STAGE_IDS = {"synthesis": 1, "pcr": 2, "aging": 3, "dilution": 4, "sequencing": 5}


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


def seed_stage(config, stage):
    """dt4dds 1.1.0 has no set_seed(); randomness comes from dt4dds.tools.rng AND the 'random' module."""
    seed = config.get("seed")
    if seed is None:
        return
    dt4dds.tools.rng = np.random.default_rng([seed, STAGE_IDS[stage]])
    random.seed(seed * 1000 + STAGE_IDS[stage])
    np.random.seed((seed * 1000 + STAGE_IDS[stage]) % (2 ** 32))


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
def clustering_enabled(config):
    cfg = config.get("clustering")
    return bool(cfg) and cfg.get("enabled", True)


def is_number(x):
    return isinstance(x, (int, float)) and not isinstance(x, bool)


def validate_config(config):
    if config.get("output_format", "fastq") not in OUTPUT_FORMATS:
        raise ValueError(f"output_format: {config['output_format']!r}, allowed: {list(OUTPUT_FORMATS)}")
    for key in ("output_merge", "output_dereplication"):
        if key in config and not isinstance(config[key], bool):
            raise ValueError(f"{key}: must be true or false, got: {config[key]!r}")

    seed = config.get("seed")
    if seed is not None and (not isinstance(seed, int) or isinstance(seed, bool) or seed < 0):
        raise ValueError(f"seed: must be an integer >= 0, got: {seed!r}")
    if seed is None:
        print("WARNING: no 'seed' in config, this run is not reproducible", file=sys.stderr)

    if shutil.which("fastp") is None:
        raise RuntimeError("'fastp' not found in PATH (conda install -c bioconda fastp)")

    n_reads = config["processes"]["sequencing"].get("n_reads", DEFAULT_N_READS)
    if not isinstance(n_reads, int) or isinstance(n_reads, bool) or n_reads < 1:
        raise ValueError(f"sequencing: 'n_reads' must be an integer >= 1, got: {n_reads!r}")

    cfg = config["processes"].get("dilution")
    if cfg:
        unknown = sorted(set(cfg) - {"percent"})
        if unknown:
            raise ValueError(f"dilution: only 'percent' is supported (share of molecules kept, relative to the "
                             f"pool at this point), got extra keys: {unknown}")
        percent = cfg.get("percent")
        if not is_number(percent) or not 0 < percent <= 100:
            raise ValueError(f"dilution: 'percent' must be a number in (0, 100], got: {percent!r}")

    if clustering_enabled(config):
        cl = config["clustering"]
        if shutil.which("vsearch") is None:
            raise RuntimeError("'vsearch' not found in PATH (conda install -c bioconda vsearch)")
        if config.get("output_format", "fastq") == "fastq":
            raise ValueError("clustering: output_format 'fastq' is not possible (consensus/centroid "
                             "sequences have no quality scores), use 'fasta' or 'txt'")
        identity = cl.get("identity", 0.8)
        if not is_number(identity) or not 0 < identity <= 1:
            raise ValueError(f"clustering: 'identity' must be a number in (0, 1], got: {identity!r}")
        if cl.get("strand", "plus") not in CLUSTER_STRANDS:
            raise ValueError(f"clustering: 'strand' must be one of {list(CLUSTER_STRANDS)}, got: {cl['strand']!r}")
        if not isinstance(cl.get("consensus", True), bool):
            raise ValueError(f"clustering: 'consensus' must be true or false, got: {cl['consensus']!r}")
        mcs = cl.get("min_cluster_size", 1)
        if not isinstance(mcs, int) or isinstance(mcs, bool) or mcs < 1:
            raise ValueError(f"clustering: 'min_cluster_size' must be an integer >= 1, got: {mcs!r}")


# ---------------------------------------------------------------------------
# Synthesis = the ONE absolute normalisation: every coalition starts from a reference pool of
# the same size (mean coverage `sampling`). Everything after that is relative.
# config: method (only "Array"), oligo_distribution_type, oligo_distribution_params,
#         sampling (mean coverage), volume
# ---------------------------------------------------------------------------
def synthesis(config, seq_list, n_seqs, primers_0):
    cfg = config["processes"]["synthesis"]

    if cfg.get("method", "Array") != "Array":
        raise ValueError(f"synthesis: unsupported method {cfg['method']!r}, only 'Array'")

    overrides = dict(
        oligo_distribution_type=cfg.get("oligo_distribution_type", "lognormal"),
        oligo_distribution_params=cfg.get("oligo_distribution_params", {"mean": 0, "sigma": 0.30}),
    )
    if cfg["is_perfect"]:
        overrides.update(deletion_rate=0, insertion_rate=0, substitution_rate=0)

    settings = dt4dds.settings.defaults.ArraySynthesis_Twist()
    array_synthesis = dt4dds.processes.ArraySynthesis(settings(**overrides))
    array_synthesis.process(seq_list)

    coverage = cfg.get("sampling", DEFAULT_SAMPLING)       # same default for ideal and real
    pool = array_synthesis.sample_by_counts(coverage * n_seqs)
    pool = dt4dds.generators.attach_primers_to_pool(pool, *primers_0)
    pool.volume = cfg.get("volume", 10)
    return pool


# ---------------------------------------------------------------------------
# PCR
# config: pcr_mode ("HiFi" -> dt4dds.settings.defaults.PCR_<mode>), primers
#         ("primers_0" | "primers_2"), template_volume, volume, efficiency_mean, n_cycles
# "is_perfect" ONLY zeroes the polymerase error rate; every other parameter is identical in both modes.
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
        overrides.update(polymerase_basesubstitutionrate=0, polymerase_fidelity=100)

    mode = cfg.get("pcr_mode", "HiFi")
    settings_cls = getattr(dt4dds.settings.defaults, f"PCR_{mode}", None)
    if settings_cls is None:
        raise ValueError(f"pcr: unknown pcr_mode {mode!r}")

    process = dt4dds.processes.PCR(settings_cls()(**overrides))
    return process.process(pool)


# ---------------------------------------------------------------------------
# Aging
# config: n_halflives. "Perfect" = no decay at all (the pool passes through unchanged).
# ---------------------------------------------------------------------------
def aging(config, pool):
    cfg = config["processes"]["aging"]
    if not cfg["is_perfect"]:
        settings = dt4dds.settings.defaults.Aging()
        process = dt4dds.processes.Aging(settings(), n_halflives=cfg.get("n_halflives", 2))
        pool = process.process(pool)
    pool.volume = AGING_VOLUME          # same volume in both modes
    return pool


# ---------------------------------------------------------------------------
# Dilution (optional, right before sequencing): RELATIVE only.
# "dilution": {"percent": 1.0}  keeps 1 % of the molecules that arrive at this point.
# Because it is relative, material lost in earlier stages is still lost afterwards
# (an absolute target would hide it).
# ---------------------------------------------------------------------------
def dilution(config, pool):
    cfg = config["processes"].get("dilution")
    if not cfg:
        return pool
    return pool.sample_by_factor(cfg["percent"] / 100.0, accurate=True)


# ---------------------------------------------------------------------------
# Sequencing-by-synthesis
# "is_perfect" ONLY zeroes the substitution rates; n_reads, read length and primers are identical.
# A fresh settings object is created each time (dt4dds shares the default one between calls).
# ---------------------------------------------------------------------------
def sequencing(config, pool, output_dir):
    cfg = config["processes"]["sequencing"]
    kwargs = dict(
        output_directory=output_dir,
        n_reads=cfg.get("n_reads", DEFAULT_N_READS),
        read_length=READ_LENGTH,
        read_mode="paired-end",
    )
    if cfg["is_perfect"]:
        kwargs["substitution_rates"] = [0, 0]
    process = dt4dds.processes.SBSSequencing(dt4dds.settings.SequencingSettings(), **kwargs)
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
# FASTQ / FASTA helpers
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


def count_reads(fastq_gz):
    with gzip.open(fastq_gz, "rt") as src:
        return sum(1 for _ in src) // 4


def iter_fasta(fasta_path):
    """Yields (header, sequence) for a FASTA file with unwrapped sequences."""
    header = None
    with open(fasta_path, "r", encoding="utf-8") as src:
        for line in src:
            line = line.strip()
            if line.startswith(">"):
                header = line[1:]
            elif line and header is not None:
                yield header, line
                header = None


# ---------------------------------------------------------------------------
# Clustering (optional, VSEARCH --cluster_fast): groups reads that come from the same oligo.
# config "clustering": {
#     "enabled": true,           # default true when the block is present
#     "identity": 0.8,           # min. sequence identity to join a cluster
#     "consensus": true,         # true: one consensus sequence per cluster (built from an MSA of its reads)
#                                # false: one representative read (centroid) per cluster, still noisy
#     "min_cluster_size": 1,     # drop clusters with fewer reads
#     "strand": "plus"           # "plus": all reads have the same orientation (true after fastp, R1 side)
# }
# Output of this step: FASTA, one record per cluster, header "...;size=<number of reads>".
# ---------------------------------------------------------------------------
def fastq_to_fasta(fastq_gz, fasta_path):
    with open(fasta_path, "w", encoding="utf-8") as dst:
        for read_id, seq, _ in iter_fastq(fastq_gz):
            dst.write(f">{read_id}\n{seq}\n")


def cluster_reads(config, fastq_gz, min_length, work_dir):
    cfg = config["clustering"]
    consensus = cfg.get("consensus", True)

    reads_fasta = work_dir / "reads_for_clustering.fasta"
    fastq_to_fasta(fastq_gz, reads_fasta)

    clusters_fasta = work_dir / ("clusters_consensus.fasta" if consensus else "clusters_centroids.fasta")
    cmd = [
        "vsearch", "--cluster_fast", str(reads_fasta),
        "--id", str(cfg.get("identity", 0.8)),
        "--strand", cfg.get("strand", "plus"),
        "--minseqlength", str(min_length),   # vsearch silently drops everything shorter than 32 by default
        "--sizeout",
        "--fasta_width", "0",
        "--threads", str(THREADS),
        "--quiet",
        "--consout" if consensus else "--centroids", str(clusters_fasta),
    ]
    result = subprocess.run(cmd, capture_output=True, text=True)
    if result.returncode != 0:
        raise RuntimeError(f"vsearch failed:\n{result.stderr}")
    return clusters_fasta


# ---------------------------------------------------------------------------
# Output: output.<fastq|fasta|txt>, optional dereplication (by sequence, first read wins)
# ---------------------------------------------------------------------------
def write_output(fastq_gz, path, fmt, dereplicate):
    seen = set()
    n = 0
    lengths = []
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
            lengths.append(len(seq))
    return n, lengths, None


def write_cluster_output(clusters_fasta, path, fmt, dereplicate, min_cluster_size):
    seen = set()
    n = 0
    lengths = []
    all_sizes = []
    with open(path, "w", encoding="utf-8") as dst:
        for header, seq in iter_fasta(clusters_fasta):
            match = re.search(r"size=(\d+)", header)
            size = int(match.group(1)) if match else 1
            all_sizes.append(size)
            if size < min_cluster_size:
                continue
            if dereplicate:
                if seq in seen:
                    continue
                seen.add(seq)
            if fmt == "fasta":
                dst.write(f">cluster_{n};size={size}\n{seq}\n")
            else:
                dst.write(seq + "\n")
            n += 1
            lengths.append(len(seq))
    return n, lengths, all_sizes


def length_summary(lengths):
    if not lengths:
        return None
    return {"min": min(lengths), "median": statistics.median(lengths), "max": max(lengths)}


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
    use_clustering = clustering_enabled(config)
    n_reads_requested = config["processes"]["sequencing"].get("n_reads", DEFAULT_N_READS)

    basic_properties()
    primers_0, primers_2 = design_primers()

    stats = {"seed": config.get("seed"), "n_reads_requested": n_reads_requested}

    with tempfile.TemporaryDirectory() as tmp:
        tmp = Path(tmp)

        sequences_file = SEQUENCES_FILE
        if FASTA_FILE.exists():
            sequences_file = tmp / "sequences.txt"
            fasta_to_txt(FASTA_FILE, sequences_file)
        seq_list = dt4dds.tools.txt_to_seqlist(sequences_file)
        min_length = max(30, int(0.7 * oligo_min_length(sequences_file)))
        stats["n_designed_oligos"] = len(seq_list)

        # molecules in the pool after every stage: shows which stage loses material
        seed_stage(config, "synthesis")
        pool = synthesis(config, seq_list, len(seq_list), primers_0)
        stats["molecules_after_synthesis"] = int(pool.n_oligos)

        seed_stage(config, "pcr")
        pool = pcr(config, pool, primers_0, primers_2)
        stats["molecules_after_pcr"] = int(pool.n_oligos)

        seed_stage(config, "aging")
        pool = aging(config, pool)
        stats["molecules_after_aging"] = int(pool.n_oligos)

        seed_stage(config, "dilution")
        pool = dilution(config, pool)
        stats["molecules_before_sequencing"] = int(pool.n_oligos)

        raw_dir = tmp / "raw"
        raw_dir.mkdir()
        seed_stage(config, "sequencing")
        sequencing(config, pool, raw_dir)

        stats["read_pairs_sequenced"] = count_reads(raw_dir / "R1.fq.gz")
        fastq_gz = preprocess(merge, primers_2, min_length, raw_dir, tmp)
        stats["reads_after_preprocessing"] = count_reads(fastq_gz)

        OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
        out_path = OUTPUT_DIR / f"output.{fmt}"
        if use_clustering:
            clusters_fasta = cluster_reads(config, fastq_gz, min_length, tmp)
            n, lengths, sizes = write_cluster_output(
                clusters_fasta, out_path, fmt, dereplicate,
                config["clustering"].get("min_cluster_size", 1))
            stats["clusters_total"] = len(sizes)
            stats["clusters_singletons"] = sum(1 for s in sizes if s == 1)
            stats["cluster_size_median"] = statistics.median(sizes) if sizes else None
            stats["cluster_size_max"] = max(sizes) if sizes else None
            stats["reads_per_cluster_mean"] = round(sum(sizes) / len(sizes), 2) if sizes else None
        else:
            n, lengths, _ = write_output(fastq_gz, out_path, fmt, dereplicate)
        stats["n_output_sequences"] = n
        stats["output_length"] = length_summary(lengths)

    with open(OUTPUT_DIR / "stats.json", "w", encoding="utf-8") as f:
        json.dump(stats, f, indent=2)

    print(f"{out_path}: {n} sequences" + (" (clusters)" if use_clustering else ""))


if __name__ == "__main__":
    main()