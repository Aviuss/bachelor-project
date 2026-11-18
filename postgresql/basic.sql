INSERT INTO resource_formats (name) VALUES ('JSON');
INSERT INTO resource_formats (name) VALUES ('TXT');
INSERT INTO resource_formats (name) VALUES ('BINARY');
INSERT INTO resource_formats (name) VALUES ('INT');
INSERT INTO resource_formats (name) VALUES ('FLOAT');
INSERT INTO resource_formats (name) VALUES ('DOUBLE');
INSERT INTO resource_formats (name) VALUES ('BOOL');
INSERT INTO resource_formats (name) VALUES ('FASTA');
INSERT INTO resource_formats (name) VALUES ('NONE');

-- test pipeline
INSERT INTO pipeline_types (name) VALUES ('dna-aeon');

INSERT INTO job_types (name) VALUES ('encode DNA-Aeon');
INSERT INTO job_type_io (job_type_id, direction, name, format_id, location_id) VALUES (
    (SELECT id FROM job_types WHERE name == 'encode DNA-Aeon'),
    'in',
    'D', -- inputFileToEncode
    (SELECT id FROM resource_formats WHERE name == 'NONE'),
    's3'
)
INSERT INTO job_type_io (job_type_id, direction, name, format_id, location_id) VALUES (
    (SELECT id FROM job_types WHERE name == 'encode DNA-Aeon'),
    'out',
    'encoded.fasta',
    (SELECT id FROM resource_formats WHERE name == 'NONE'),
    's3'
)
INSERT INTO job_type_io (job_type_id, direction, name, format_id, location_id) VALUES (
    (SELECT id FROM job_types WHERE name == 'encode DNA-Aeon'),
    'out',
    'encoded.ini',
    (SELECT id FROM resource_formats WHERE name == 'NONE'),
    's3'
)

INSERT INTO job_types (name) VALUES ('decode DNA-Aeon');
INSERT INTO job_type_io (job_type_id, direction, name, format_id, location_id) VALUES (
    (SELECT id FROM job_types WHERE name == 'decode DNA-Aeon'),
    'in',
    'originalData',
    (SELECT id FROM resource_formats WHERE name == 'NONE'),
    's3'
)
INSERT INTO job_type_io (job_type_id, direction, name, format_id, location_id) VALUES (
    (SELECT id FROM job_types WHERE name == 'decode DNA-Aeon'),
    'in',
    'encoded.fasta',
    (SELECT id FROM resource_formats WHERE name == 'NONE'),
    's3'
)
INSERT INTO job_type_io (job_type_id, direction, name, format_id, location_id) VALUES (
    (SELECT id FROM job_types WHERE name == 'decode DNA-Aeon'),
    'in',
    'encoded.ini',
    (SELECT id FROM resource_formats WHERE name == 'NONE'),
    's3'
)
INSERT INTO job_type_io (job_type_id, direction, name, format_id, location_id) VALUES (
    (SELECT id FROM job_types WHERE name == 'decode DNA-Aeon'),
    'out',
    'result',
    (SELECT id FROM resource_formats WHERE name == 'NONE'),
    's3'
)