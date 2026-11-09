# Używamy oryginalnego obrazu jako bazy
FROM agimpel/dt4dds-benchmark:latest

# Uprawnienia roota, aktualizacja pakietów i instalacja nano
USER root
RUN apt-get update && apt-get install -y nano

RUN pip3 install matplotlib pandas

# Ustawiamy domyślny katalog roboczy na /data, żeby nie trzeba było do niego przechodzić
WORKDIR /data
# Podpiąć komendą folder
COPY ./data/dt4dds_main.py .

ENTRYPOINT ["python", "dt4dds_main.py", "--"]