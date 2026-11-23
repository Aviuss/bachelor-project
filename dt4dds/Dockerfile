# Używamy oryginalnego obrazu jako bazy
FROM agimpel/dt4dds-benchmark:latest

RUN apt-get update && apt-get install -y --no-install-recommends \
    nano \
    build-essential \
    zlib1g-dev \
    git \
    fastp \
 && rm -rf /var/lib/apt/lists/*

RUN pip3 install --no-cache-dir matplotlib pandas cutadapt

RUN git clone --depth 1 https://github.com/jsh58/NGmerge.git /tmp/NGmerge \
 && make -C /tmp/NGmerge \
 && install -m 0755 /tmp/NGmerge/NGmerge /usr/local/bin/NGmerge \
 && rm -rf /tmp/NGmerge

# Ustawiamy domyślny katalog roboczy na /data, żeby nie trzeba było do niego przechodzić
WORKDIR /data
# Podpiąć komendą folder
COPY ./data/dt4dds_main.py .

ENTRYPOINT ["python", "dt4dds_main.py", "--"]