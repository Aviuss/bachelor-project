# bachelor-project

## running code

```
sudo docker compose -f postgresql/docker-compose-only-db.yml up -d
sudo docker compose -f garage_s3/docker-compose-only-s3.yml up -d
```


```
cd server/Master
set -a; . ../../postgresql/db.env; . ../../garage_s3/s3.env; set +a
dotnet run
```

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

### generating token for new machine:

`dotnet run -- add-machine`
