CREATE SCHEMA IF NOT EXISTS synco;

CREATE TYPE synco.citizen_education_level AS ENUM (
    'uneducated',
    'poorly_educated',
    'educated',
    'well_educated',
    'highly_educated'
);

CREATE TYPE synco.commodity AS ENUM (
    'grain',
    'convenience_food',
    'food',
    'vegetables',
    'meals',
    'wood',
    'timber',
    'paper',
    'furniture',
    'vehicles',
    'lodging',
    'outgoing_mail',
    'local_mail',
    'unsorted_mail',
    'oil',
    'petrochemicals',
    'ore',
    'plastics',
    'metals',
    'electronics',
    'software',
    'coal',
    'stone',
    'livestock',
    'cotton',
    'steel',
    'minerals',
    'concrete',
    'machinery',
    'chemicals',
    'pharmaceuticals',
    'beverages',
    'textiles',
    'telecom',
    'financial',
    'media',
    'entertainment',
    'recreation',
    'garbage',
    'fish'
);

CREATE TYPE synco.cargo_method AS ENUM (
    'train',
    'ship',
    'airplane',
    'truck'
);

CREATE TYPE synco.transit_method AS ENUM (
    'bus',
    'tram',
    'subway',
    'train',
    'ship',
    'ferry',
    'airplane',
    'taxi'
);

CREATE TYPE synco.building_zone_type AS ENUM (
    'residential',
    'commercial',
    'industrial',
    'office',
    'other'
);

CREATE TABLE IF NOT EXISTS synco.city_data (
    id SERIAL PRIMARY KEY,
    date TIMESTAMPTZ NOT NULL,
    name VARCHAR(32) NOT NULL,
    collected_mail INTEGER NOT NULL DEFAULT 0,
    delivered_mail INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS synco.demographics (
    id SERIAL PRIMARY KEY,
    snapshot_id INTEGER NOT NULL REFERENCES synco.city_data(id) ON DELETE CASCADE,
    children INTEGER NOT NULL DEFAULT 0,
    teens INTEGER NOT NULL DEFAULT 0,
    adults INTEGER NOT NULL DEFAULT 0,
    seniors INTEGER NOT NULL DEFAULT 0,
    birth_rate INTEGER NOT NULL DEFAULT 0,
    death_rate INTEGER NOT NULL DEFAULT 0,
    uneducated INTEGER NOT NULL DEFAULT 0,
    poorly_educated INTEGER NOT NULL DEFAULT 0,
    educated INTEGER NOT NULL DEFAULT 0,
    well_educated INTEGER NOT NULL DEFAULT 0,
    highly_educated INTEGER NOT NULL DEFAULT 0,
    households INTEGER NOT NULL DEFAULT 0,
    household_wealth INTEGER NOT NULL DEFAULT 0,
    homeless INTEGER NOT NULL DEFAULT 0,
    tourists INTEGER NOT NULL DEFAULT 0,
    lodging_total INTEGER NOT NULL DEFAULT 0,
    lodging_used INTEGER NOT NULL DEFAULT 0,
    CONSTRAINT sanity_check_matching_population CHECK (
        (uneducated + poorly_educated + educated + well_educated + highly_educated) =
        (children + teens + adults + seniors)
    )
);

CREATE TABLE IF NOT EXISTS synco.wellbeing (
    id SERIAL PRIMARY KEY,
    snapshot_id INTEGER NOT NULL REFERENCES synco.city_data(id) ON DELETE CASCADE,
    wellbeing INTEGER NOT NULL DEFAULT 0,
    health INTEGER NOT NULL DEFAULT 0,
    crime_count INTEGER NOT NULL DEFAULT 0,
    crime_rate INTEGER NOT NULL DEFAULT 0,
    escaped_arrests INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS synco.economy (
    id SERIAL PRIMARY KEY,
    snapshot_id INTEGER NOT NULL REFERENCES synco.city_data(id) ON DELETE CASCADE,
    balance INTEGER NOT NULL DEFAULT 0,
    residential_tax_income INTEGER NOT NULL DEFAULT 0,
    commercial_tax_income INTEGER NOT NULL DEFAULT 0,
    industrial_tax_income INTEGER NOT NULL DEFAULT 0,
    office_tax_income INTEGER NOT NULL DEFAULT 0,
    electricity_export_income INTEGER NOT NULL DEFAULT 0,
    water_export_income INTEGER NOT NULL DEFAULT 0,
    education_fee_income INTEGER NOT NULL DEFAULT 0,
    healthcare_fee_income INTEGER NOT NULL DEFAULT 0,
    parking_fee_income INTEGER NOT NULL DEFAULT 0,
    transport_fee_income INTEGER NOT NULL DEFAULT 0,
    garbage_fee_income INTEGER NOT NULL DEFAULT 0,
    electricity_fee_income INTEGER NOT NULL DEFAULT 0,
    water_fee_income INTEGER NOT NULL DEFAULT 0,
    government_subsidy_income INTEGER NOT NULL DEFAULT 0,
    tourist_income INTEGER NOT NULL DEFAULT 0,
    service_upkeep_expense INTEGER NOT NULL DEFAULT 0,
    loan_interest_expense INTEGER NOT NULL DEFAULT 0,
    electricity_import_expense INTEGER NOT NULL DEFAULT 0,
    water_import_expense INTEGER NOT NULL DEFAULT 0,
    sewage_export_expense INTEGER NOT NULL DEFAULT 0,
    commercial_subsidy_expense INTEGER NOT NULL DEFAULT 0,
    industrial_subsidy_expense INTEGER NOT NULL DEFAULT 0,
    residential_subsidy_expense INTEGER NOT NULL DEFAULT 0,
    office_subsidy_expense INTEGER NOT NULL DEFAULT 0,
    police_import_expense INTEGER NOT NULL DEFAULT 0,
    ambulance_import_expense INTEGER NOT NULL DEFAULT 0,
    garbage_import_expense INTEGER NOT NULL DEFAULT 0,
    hearse_import_expense INTEGER NOT NULL DEFAULT 0,
    fire_import_expense INTEGER NOT NULL DEFAULT 0,
    map_tile_upkeep_expense INTEGER NOT NULL DEFAULT 0,
    service_wealth INTEGER NOT NULL DEFAULT 0,
    processing_wealth INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS synco.residential_tax_rate (
    id SERIAL PRIMARY KEY,
    snapshot_id INTEGER NOT NULL REFERENCES synco.city_data(id) ON DELETE CASCADE,
    education_level synco.citizen_education_level NOT NULL,
    rate SMALLINT NOT NULL
);

CREATE TABLE IF NOT EXISTS synco.commercial_tax_rate (
    id SERIAL PRIMARY KEY,
    snapshot_id INTEGER NOT NULL REFERENCES synco.city_data(id) ON DELETE CASCADE,
    commodity synco.commodity NOT NULL,
    rate SMALLINT NOT NULL
);

CREATE TABLE IF NOT EXISTS synco.industrial_tax_rate (
    id SERIAL PRIMARY KEY,
    snapshot_id INTEGER NOT NULL REFERENCES synco.city_data(id) ON DELETE CASCADE,
    commodity synco.commodity NOT NULL,
    rate SMALLINT NOT NULL
);

CREATE TABLE IF NOT EXISTS synco.office_tax_rate (
    id SERIAL PRIMARY KEY,
    snapshot_id INTEGER NOT NULL REFERENCES synco.city_data(id) ON DELETE CASCADE,
    commodity synco.commodity NOT NULL,
    rate SMALLINT NOT NULL
);

CREATE TABLE IF NOT EXISTS synco.trade (
    id SERIAL PRIMARY KEY,
    snapshot_id INTEGER NOT NULL REFERENCES synco.city_data(id) ON DELETE CASCADE,
    commodity synco.commodity NOT NULL,
    quantity INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS synco.cargo (
    id SERIAL PRIMARY KEY,
    snapshot_id INTEGER NOT NULL REFERENCES synco.city_data(id) ON DELETE CASCADE,
    method synco.cargo_method NOT NULL,
    count INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS synco.transit (
    id SERIAL PRIMARY KEY,
    snapshot_id INTEGER NOT NULL REFERENCES synco.city_data(id) ON DELETE CASCADE,
    method synco.transit_method NOT NULL,
    passenger_count INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS synco.labor (
    id SERIAL PRIMARY KEY,
    snapshot_id INTEGER NOT NULL REFERENCES synco.city_data(id) ON DELETE CASCADE,
    workers INTEGER NOT NULL DEFAULT 0,
    unemployed INTEGER NOT NULL DEFAULT 0,
    senior_worker_in_demand_percentage INTEGER NOT NULL DEFAULT 0,
    city_service_workers INTEGER NOT NULL DEFAULT 0,
    city_service_max_workers INTEGER NOT NULL DEFAULT 0,
    processing_workers INTEGER NOT NULL DEFAULT 0,
    processing_max_workers INTEGER NOT NULL DEFAULT 0,
    processing_count INTEGER NOT NULL DEFAULT 0,
    service_workers INTEGER NOT NULL DEFAULT 0,
    service_max_workers INTEGER NOT NULL DEFAULT 0,
    service_count INTEGER NOT NULL DEFAULT 0
);

-- Slowly changing dimension - keyed by (entity_id, city_name)
-- not snapshot_id so nodes aren't duplicated every snapshot
CREATE TABLE IF NOT EXISTS synco.node (
    entity_id BIGINT NOT NULL,
    city_name VARCHAR(32) NOT NULL,
    x REAL NOT NULL,
    y REAL NOT NULL,
    z REAL NOT NULL,
    first_seen TIMESTAMPTZ NOT NULL,
    last_seen TIMESTAMPTZ NOT NULL,
    deleted_at TIMESTAMPTZ,
    PRIMARY KEY (entity_id, city_name)
);

-- Slowly changing dimension - same reasoning as node
CREATE TABLE IF NOT EXISTS synco.edge (
    entity_id BIGINT NOT NULL,
    city_name VARCHAR(32) NOT NULL,
    version INTEGER NOT NULL,
    start_node_id BIGINT NOT NULL,
    end_node_id BIGINT NOT NULL,
    ax REAL NOT NULL, ay REAL NOT NULL, az REAL NOT NULL,
    bx REAL NOT NULL, by REAL NOT NULL, bz REAL NOT NULL,
    cx REAL NOT NULL, cy REAL NOT NULL, cz REAL NOT NULL,
    dx REAL NOT NULL, dy REAL NOT NULL, dz REAL NOT NULL,
    elevation_min REAL NOT NULL DEFAULT 0,
    elevation_max REAL NOT NULL DEFAULT 0,
    first_seen TIMESTAMPTZ NOT NULL,
    last_seen TIMESTAMPTZ NOT NULL,
    deleted_at TIMESTAMPTZ,
    PRIMARY KEY (entity_id, city_name),
    FOREIGN KEY (start_node_id, city_name) REFERENCES synco.node(entity_id, city_name),
    FOREIGN KEY (end_node_id, city_name) REFERENCES synco.node(entity_id, city_name)
);

CREATE TABLE IF NOT EXISTS synco.edge_service_coverage (
    id SERIAL PRIMARY KEY,
    snapshot_id INTEGER NOT NULL REFERENCES synco.city_data(id) ON DELETE CASCADE,
    edge_entity_id BIGINT NOT NULL,
    city_name VARCHAR(32) NOT NULL,
    service_index INTEGER NOT NULL,
    coverage_min REAL NOT NULL DEFAULT 0,
    coverage_max REAL NOT NULL DEFAULT 0,
    FOREIGN KEY (edge_entity_id, city_name) REFERENCES synco.edge(entity_id, city_name)
        ON DELETE CASCADE
);

-- Static building properties - slowly changing dimension
CREATE TABLE IF NOT EXISTS synco.building (
    entity_id BIGINT NOT NULL,
    city_name VARCHAR(32) NOT NULL,
    version INTEGER NOT NULL DEFAULT 1,
    zone_type synco.building_zone_type NOT NULL,
    commercial_resources TEXT,
    industrial_resources TEXT,
    pos_x REAL NOT NULL,
    pos_y REAL NOT NULL,
    pos_z REAL NOT NULL,
    road_edge_id BIGINT,
    city_name_edge VARCHAR(32),
    curve_position REAL NOT NULL DEFAULT 0,
    has_water_node BOOLEAN NOT NULL DEFAULT FALSE,
    has_sewage_node BOOLEAN NOT NULL DEFAULT FALSE,
    has_electricity_node BOOLEAN NOT NULL DEFAULT FALSE,
    is_school BOOLEAN NOT NULL DEFAULT FALSE,
    is_hospital BOOLEAN NOT NULL DEFAULT FALSE,
    first_seen TIMESTAMPTZ NOT NULL,
    last_seen TIMESTAMPTZ NOT NULL,
    deleted_at TIMESTAMPTZ,
    PRIMARY KEY (entity_id, city_name),
    FOREIGN KEY (road_edge_id, city_name_edge) REFERENCES synco.edge(entity_id, city_name)
);

-- Time-varying building state - one row per building per snapshot
CREATE TABLE IF NOT EXISTS synco.building_snapshot (
    id SERIAL PRIMARY KEY,
    snapshot_id INTEGER NOT NULL REFERENCES synco.city_data(id) ON DELETE CASCADE,
    entity_id BIGINT NOT NULL,
    city_name VARCHAR(32) NOT NULL,
    electricity_wanted INTEGER NOT NULL DEFAULT 0,
    electricity_fulfilled INTEGER NOT NULL DEFAULT 0,
    electricity_connected BOOLEAN NOT NULL DEFAULT FALSE,
    water_wanted INTEGER NOT NULL DEFAULT 0,
    water_fulfilled INTEGER NOT NULL DEFAULT 0,
    sewage_fulfilled INTEGER NOT NULL DEFAULT 0,
    water_pollution REAL NOT NULL DEFAULT 0,
    water_connected BOOLEAN NOT NULL DEFAULT FALSE,
    sewage_connected BOOLEAN NOT NULL DEFAULT FALSE,
    service_available INTEGER NOT NULL DEFAULT 0,
    service_mean_priority REAL NOT NULL DEFAULT 0,
    FOREIGN KEY (entity_id, city_name) REFERENCES synco.building(entity_id, city_name)
);
