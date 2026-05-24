# Telex Telemetry Data & Mapping Specification

This document provides the layout and data-mapping specification for the Telex mod metrics collection system. Telex hooks into the Cities Skylines 2 simulation pipeline (GameSimulation phase) to poll, normalize, and publish high-fidelity city states via MQTT JSON payloads.

---

## 1. Core Architectural Quirks & Fixed Constraints

### Asynchronous Staging Execution
* Problem: System values in the game’s unmanaged C# arrays are tied to background ECS thread workers. Reading the buffer directly during a standard system update pass can yield uninitialized (0) arrays.
* Resolution Rule: The custom simulation pass must explicitly force background execution barriers to finish before serializing variables to prevent stale data.

this.Dependency.Complete(); // Force execution pipelines to sync memory states

### Lazy Loading Initialization
* Behavior: Sectors or services that are locked behind milestones (e.g., Office properties early game) are completely bypassed by the engine’s demand and tracking systems.
* Schema Impact: Corresponding metrics fields will emit clean 0 primitives until a development threshold forces initialization. Databases should expect structural blocks to remain default-padded until unlocked.

---

## 2. Tax Rates Data & Calculation Schema

The game tracks tax structures in an optimized unmanaged signed integer buffer (NativeArray<int>) using a Base + Offset distance topology. 

### The Math Matrix

The game considers a raw value of 10 (the default 10% tax baseline) as the zero-point (0) marker inside memory arrays. Array entities store the relative step deviation from that baseline rather than the absolute value displayed in the user interface.

Telemetry Value = Active UI Percentage - 10

### Mapping State Examples
| Desired UI Tax Rate | Array Numeric Representation | Economic Category Behavior |
| :--- | :--- | :--- |
| 16% | +6 | Increased taxation penalty |
| 10% | 0 | Default baseline zero-point |
| 8% | -2 | Active production subsidy |

### Memory Offset Layout
* Residential Base: Located at taxRates[1]
  * Sub-level calculation: Base + taxRates[5 + Educational/Job Level] (Iterates 0 through 4).
* Commercial Base: Located at taxRates[2]
  * Sub-resource calculation: Base + taxRates[10 + ResourceIndex]
* Industrial Base: Located at taxRates[3]
  * Sub-resource calculation: Base + taxRates[10 + ResourceIndex]
* Office Base: Located at taxRates[4]
  * Sub-resource calculation: Base + taxRates[10 + ResourceIndex]

---

## 3. Telemetry Stream Definitions (telex-daily)

The primary streaming snapshot emits a flat-nested document recording demographical shifting, fiscal health, logistics volume, and trade dependencies.

### Metric Category Specifications

#### Metadata Block
* type (string): Identifies payload grouping ("daily", "graph", "demand", "buildings").
* city_name (string): Live user-configured target city string.
* day / year / month / hour / calendar_day (int): Internal Simulation Time arrays.

#### Demographic Profiles
* population (int): Living entities with resolved real-estate ownership.
* population_with_move_in (int): Active tracking aggregate including entities currently routing through map border nodes towards properties.
* adults / age / households / homeless (int): Core social health segments.
* moved_in / moved_away (int): Volume shift indicators.
* moved_away_* (int): Direct issue buckets tracking outbound migration vectors:
  * no_suitable_property (Index 1)
  * not_happy (Index 2)
  * no_adults (Index 3)
  * no_money (Index 4)
  * tourist_no_target / tourist_no_hotel / tourist_no_money (Indices 5-7)
  * trip_not_moved_in (Index 8)

#### Educational Pipeline
* education_uneducated (int): Tier 0 workforce capability.
* education_poorly_educated (int): Tier 1 workforce capability (Default baseline profile for incoming outside connections).
* education_educated (int): Tier 2 workforce capability.
* education_well_educated (int): Tier 3 workforce capability.
* education_highly_educated (int): Tier 4 workforce capability.

#### Financial Primitives
* money (int): Absolute city liquid currency pool.
* income (int): Global city revenues generated via utility rates and raw asset ticks.
* expense (int): Municipal drain variables.
* *_taxable_income (int): Segmented industry raw ledger values tracking foundational capital strength per sector before active multipliers are applied.

#### Resource Trade Map
* trade_by_resource (Map String -> Int): Metric index calculating resource reliance metrics via EconomyUtils.GetResourceIndex(). High numbers in early-game steps indicate localized production deficits met through outside logistics channels (e.g., Coal tracking to supply municipal energy requirements).

---

## 4. DB Schema Mapping Recommendations

When loading this structure into your target analytical engine (such as Parquet files or relational systems), implement the following layout properties:

1. Partition Strategy: Partition your database tables or cold-storage object structures using the composite key of city_name and day (or year/month) to streamline temporal queries.
2. Tax Rate Optimization: Do not store the raw array offsets (-2, +6) as-is without a view translation. Store them directly as adjusted analytical percentages (0.08, 0.16) by applying an offset + 10 calculation step inside your pipeline ingestion layer.
3. Sparse Columns: Maintain tax_rates_office and individual resource metrics fields as nullable or default-zeroed columns to handle early-game simulation states smoothly.
