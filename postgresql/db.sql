CREATE TYPE job_status AS ENUM (
	'pending',
	'ready',
	'assigned',
	'running',
	'succeeded',
	'failed',
	'cancelled'
);
CREATE TYPE resource_status AS ENUM ('pending','available','failed','deleted');
CREATE TYPE io_direction    AS ENUM ('in','out');

CREATE TABLE pipeline_types (
	id   int  GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
	name text NOT NULL UNIQUE
);

CREATE TABLE job_types (
	id   int  GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
	name text NOT NULL UNIQUE
);

CREATE TABLE resource_formats (
	id   int  GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
	name text NOT NULL UNIQUE
);

CREATE TABLE job_type_io (
	id          int GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
	job_type_id int          NOT NULL REFERENCES job_types(id),
	direction   io_direction NOT NULL,
	name        text         NOT NULL,
	format_id   int          NOT NULL REFERENCES resource_formats(id),
	UNIQUE (job_type_id, direction, name)
);

CREATE TABLE machines (
	id                 uuid  PRIMARY KEY DEFAULT gen_random_uuid(),
	token_hash         bytea NOT NULL UNIQUE,
	memory_capacity_mb int   CHECK (memory_capacity_mb > 0),
	cpu_capacity       numeric(5,2) CHECK (cpu_capacity > 0),
	last_seen_at       timestamptz
);

CREATE TABLE requests (
	id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	pipeline_type_id int  NOT NULL REFERENCES pipeline_types(id),
	version          text NOT NULL,
	email            text,
	created_at       timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE jobs (
	id          int   GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
	request_id  uuid  NOT NULL REFERENCES requests(id),
	name        text  NOT NULL,
	job_type_id int   NOT NULL REFERENCES job_types(id),
	params      jsonb NOT NULL,
	UNIQUE (request_id, name)
);

CREATE TABLE job_attempts (
	id           int    GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
	job_id       int    NOT NULL REFERENCES jobs(id),
	status       job_status NOT NULL DEFAULT 'pending',
	mem_mb       int    NOT NULL CHECK (mem_mb > 0),
	cpu          numeric(5,2) NOT NULL CHECK (cpu > 0),
	timeout_s    int    NOT NULL CHECK (timeout_s > 0),
	machine_id   uuid   REFERENCES machines(id),
	progress     smallint CHECK (progress BETWEEN 0 AND 100),
	peak_mem_mb  int,
	cpu_seconds  double precision,
	error        text,
	created_at   timestamptz NOT NULL DEFAULT now(),
	assigned_at  timestamptz,
	started_at   timestamptz,
	finished_at  timestamptz,
	CHECK (status IN ('pending','ready','cancelled')
		OR (machine_id IS NOT NULL AND assigned_at IS NOT NULL))
);

CREATE TABLE resources (
	id              uuid    PRIMARY KEY DEFAULT gen_random_uuid(),
	producer_job_id int     REFERENCES jobs(id),
	producer_io_id  int     REFERENCES job_type_io(id),
	produced_by     int     REFERENCES job_attempts(id),
	original_name   text,
	content_type    text    NOT NULL DEFAULT 'application/octet-stream',
	compressed      boolean NOT NULL DEFAULT false,
	size_bytes      bigint  CHECK (size_bytes >= 0),
	sha256          bytea   CHECK (octet_length(sha256) = 32),
	status          resource_status NOT NULL DEFAULT 'pending',
	created_at      timestamptz NOT NULL DEFAULT now(),
	UNIQUE (producer_job_id, producer_io_id),
	CHECK ((producer_job_id IS NULL) = (producer_io_id IS NULL)),
	CHECK (produced_by IS NULL OR producer_job_id IS NOT NULL)
);

CREATE TABLE job_io_resource_map (
	job_id         int  NOT NULL REFERENCES jobs(id),
	job_type_io_id int  NOT NULL REFERENCES job_type_io(id),
	resource_id    uuid NOT NULL REFERENCES resources(id),
	PRIMARY KEY (job_id, job_type_io_id)
);

CREATE VIEW job_current AS
SELECT DISTINCT ON (a.job_id)
	a.job_id, a.id AS attempt_id, a.status, a.progress, a.machine_id, a.error
FROM job_attempts a
ORDER BY a.job_id, a.id DESC;

CREATE VIEW job_edges AS
SELECT r.producer_job_id  AS from_job,
	r.producer_io_id   AS from_io,
	m.job_id           AS to_job,
	m.job_type_io_id   AS to_io,
	r.id               AS resource_id
FROM job_io_resource_map m
JOIN resources r ON r.id = m.resource_id
WHERE r.producer_job_id IS NOT NULL;

CREATE VIEW machines_status AS
SELECT m.*,
	m.last_seen_at > now() - interval '2 minutes' AS alive,
	m.memory_capacity_mb - COALESCE(r.mem, 0)     AS free_memory_mb,
	m.cpu_capacity       - COALESCE(r.cpu, 0)     AS free_cpu
FROM machines m
LEFT JOIN (SELECT machine_id, SUM(mem_mb) AS mem, SUM(cpu) AS cpu
		FROM job_attempts
		WHERE status IN ('assigned','running')
		GROUP BY machine_id) r ON r.machine_id = m.id;