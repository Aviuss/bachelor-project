# bachelor-project

## setting up postgres and s3

```
sudo docker compose -f postgresql/docker-compose-only-db.yml up -d
sudo docker compose -f garage_s3/docker-compose-only-s3.yml up -d
```

(see in Master and Agent section)

Turn off docker's:
```
sudo docker compose -f postgresql/docker-compose-only-db.yml down
sudo docker compose -f garage_s3/docker-compose-only-s3.yml down
```

## talking with garage s3

`cd garage_s3`
`alias garage='sudo docker exec -ti s3 /garage'`
`garage status`

more here: https://www.glukhov.org/data-infrastructure/object-storage/garage-quickstart/

## Master

```
cd ./server/Master
set -a; source master.env; set +a
dotnet run
```

### generating token for new machine:

`dotnet run -- add-machine`

## Agent

```
cd ./server/Agent
set -a; source agent.env; set +a
dotnet run
```

