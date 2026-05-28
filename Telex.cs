using System;
using Colossal.Logging;
using Game;
using Game.Buildings;
using Game.Citizens;
using Game.City;
using Game.Common;
using Game.Companies;
using Game.Economy;
using Game.Modding;
using Game.Simulation;
using Game.Tools;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Newtonsoft.Json;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace Telex
{
    public class Mod : IMod
    {
        public static ILog log = LogManager.GetLogger($"{nameof(Telex)}.{nameof(Mod)}").SetShowsErrorsInUI(false);

        public void OnLoad(UpdateSystem updateSystem)
        {
            updateSystem.UpdateAt<TelexSystem>(SystemUpdatePhase.GameSimulation);
        }

        public void OnDispose() { }

    }

    public partial class TelexSystem : GameSystemBase
    {
        private CitySystem m_CitySystem;
        private CityStatisticsSystem m_CityStatisticsSystem;
        private SimulationSystem m_SimulationSystem;
        private TaxSystem m_TaxSystem;
        private EntityQuery m_TimeDataQuery;
        private EntityQuery m_EdgeQuery;
        private TimeSystem m_TimeSystem;
        private CityConfigurationSystem m_CityConfigurationSystem;
        private CommercialDemandSystem m_CommercialDemandSystem;
        private IndustrialDemandSystem m_IndustrialDemandSystem;
        private ResidentialDemandSystem m_ResidentialDemandSystem;
        private EntityQuery m_BuildingQuery;
        private EntityQuery m_DemandParameterQuery;
        private CityServiceBudgetSystem m_CityServiceBudgetSystem;
        private EntityQuery m_TrafficQuery;

        private uint m_LastHour;
        private const uint kFramesPerHour = 262144 / 24;

        private IMqttClient m_MqttClient;
        private const string kMqttHost = "localhost";
        private const int kMqttPort = 1883;
        private bool m_IsFirstFrame = true;

        // MoveAwayReason enum order (0=None, skip)
        // 1=NoSuitableProperty, 2=NotHappy, 3=NoAdults, 4=NoMoney
        // 5=TouristNoTarget, 6=TouristNoHotel, 7=TouristNoMoney, 8=TripNeedNotMovedIn

        // Resource enum is a bitmask (ulong flags), EconomyUtils.GetResourceIndex
        // converts to a linear index for statistic lookups
        private static readonly Resource[] kTradeResources = new Resource[]
        {
            Resource.Grain, Resource.ConvenienceFood, Resource.Food, Resource.Vegetables,
            Resource.Meals, Resource.Wood, Resource.Timber, Resource.Paper, Resource.Furniture,
            Resource.Vehicles, Resource.Lodging, Resource.UnsortedMail, Resource.LocalMail,
            Resource.OutgoingMail, Resource.Oil, Resource.Petrochemicals, Resource.Ore,
            Resource.Plastics, Resource.Metals, Resource.Electronics, Resource.Software,
            Resource.Coal, Resource.Stone, Resource.Livestock, Resource.Cotton, Resource.Steel,
            Resource.Minerals, Resource.Concrete, Resource.Machinery, Resource.Chemicals,
            Resource.Pharmaceuticals, Resource.Beverages, Resource.Textiles, Resource.Telecom,
            Resource.Financial, Resource.Media, Resource.Entertainment, Resource.Recreation,
            Resource.Garbage, Resource.Fish
        };

        protected override void OnCreate()
        {
            m_CitySystem = World.GetOrCreateSystemManaged<CitySystem>();
            m_CityStatisticsSystem = World.GetOrCreateSystemManaged<CityStatisticsSystem>();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_TaxSystem = World.GetOrCreateSystemManaged<TaxSystem>();
            m_CityConfigurationSystem = World.GetOrCreateSystemManaged<CityConfigurationSystem>();

            m_TimeDataQuery = GetEntityQuery(ComponentType.ReadOnly<Game.Common.TimeData>());

            m_EdgeQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Net.Edge>(),
                ComponentType.ReadOnly<Game.Net.Curve>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>()
            );

            m_TimeSystem = World.GetOrCreateSystemManaged<TimeSystem>();
            m_CommercialDemandSystem = World.GetOrCreateSystemManaged<CommercialDemandSystem>();
            m_IndustrialDemandSystem = World.GetOrCreateSystemManaged<IndustrialDemandSystem>();
            m_ResidentialDemandSystem = World.GetOrCreateSystemManaged<ResidentialDemandSystem>();

            m_BuildingQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Buildings.Building>(),
                ComponentType.ReadOnly<Game.Objects.Transform>(),
                ComponentType.ReadOnly<Game.Prefabs.PrefabRef>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>()
            );

            m_DemandParameterQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Prefabs.DemandParameterData>()
            );

            m_CityServiceBudgetSystem = World.GetOrCreateSystemManaged<CityServiceBudgetSystem>();
        
            m_LastHour = uint.MaxValue;

            // Per-lane traffic flow query (CarLane sublanes that have LaneFlow)
            m_TrafficQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Net.CarLane>(),
                ComponentType.ReadOnly<Game.Net.LaneFlow>(),
                ComponentType.ReadOnly<Game.Net.EdgeLane>(),
                ComponentType.ReadOnly<Game.Net.Lane>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>()
            );

            ConnectMqtt();
        }

        protected override void OnDestroy()
        {
            if (m_MqttClient != null && m_MqttClient.IsConnected)
            {
                m_MqttClient.DisconnectAsync(
                    new MqttClientDisconnectOptionsBuilder()
                        .WithReason(MqttClientDisconnectOptionsReason.NormalDisconnection)
                        .Build()
                ).GetAwaiter().GetResult();
            }
            m_MqttClient?.Dispose();
            base.OnDestroy();
        }

        private void ConnectMqtt()
        {
            try
            {
                var mqttFactory = new MqttFactory();
                m_MqttClient = mqttFactory.CreateMqttClient();
                var options = new MqttClientOptionsBuilder()
                    .WithTcpServer(kMqttHost, kMqttPort)
                    .WithProtocolVersion(MqttProtocolVersion.V500)
                    .Build();
                m_MqttClient.ConnectAsync(options).GetAwaiter().GetResult();
                Mod.log.Info("Telex: MQTT connected.");
            }
            catch (Exception ex)
            {
                Mod.log.Error($"Telex: MQTT connection failed: {ex.Message}");
                m_MqttClient = null;
            }
        }

        private void Publish(string topic, object payload)
        {
            Mod.log.Info($"Telex: Attempting publish to {topic}");
    
            if (m_MqttClient == null || !m_MqttClient.IsConnected)
            {
                Mod.log.Warn($"Telex: MQTT not connected when publishing to {topic}, attempting reconnect...");
                ConnectMqtt();
                if (m_MqttClient == null || !m_MqttClient.IsConnected)
                {
                    Mod.log.Error($"Telex: Reconnect failed, dropping message to {topic}");
                    return;
                }
            }

            try
            {
                var json = JsonConvert.SerializeObject(payload);
                Mod.log.Info($"Telex: Publishing {json.Length} bytes to {topic}");
                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(Encoding.UTF8.GetBytes(json))
                    .Build();

                m_MqttClient.PublishAsync(message).GetAwaiter().GetResult();
                Mod.log.Info($"Telex: Published to {topic}");
            }
            catch (Exception ex)
            {
                Mod.log.Error($"Telex: Failed to publish to {topic}: {ex.Message}");
            }
        }   
        protected override void OnUpdate()
        {
            var timeData = m_TimeDataQuery.GetSingleton<Game.Common.TimeData>();
            uint currentHour = (m_SimulationSystem.frameIndex - timeData.m_FirstFrame) / kFramesPerHour;

            if (currentHour == m_LastHour)
            {
                return;
            }
            if (m_IsFirstFrame)
            {
                m_LastHour = currentHour;
                m_IsFirstFrame = false;
                return;
            }

            m_LastHour = currentHour;

            var currentDate = m_TimeSystem.GetCurrentDateTime();
            string dateString = currentDate.ToString("yyy-dd-MM'T'HH:mm:ss.fff'Z'");
            int absoluteDay = TimeSystem.GetDay(m_SimulationSystem.frameIndex, timeData);

            WriteDailySnapshot(absoluteDay, dateString);
            WriteGraphSnapshot(absoluteDay, dateString);
            WriteTrafficSnapshot(absoluteDay, dateString);
            WriteBuildingSnapshot(absoluteDay, dateString);
            //WriteDemandSnapshot(absoluteDay, dateString);
        }

        public static int GetTaxRateForResource(TaxAreaType areaType, int rawResourceInt, NativeArray<int> taxRates)
        {
            Resource resourceEnum = (Resource)rawResourceInt; 

            int resourceIndex = EconomyUtils.GetResourceIndex(resourceEnum);
            if (resourceIndex == -1) return 0;

            return areaType switch
            {
                TaxAreaType.Residential => taxRates[(int)TaxAreaType.Residential] + taxRates[5 + rawResourceInt], // jobLevel directly
                TaxAreaType.Commercial  => taxRates[(int)TaxAreaType.Commercial]  + taxRates[10 + resourceIndex],
                TaxAreaType.Industrial  => taxRates[(int)TaxAreaType.Industrial]  + taxRates[10 + resourceIndex],
                TaxAreaType.Office      => taxRates[(int)TaxAreaType.Office]      + taxRates[10 + resourceIndex],
                _ => 0
            };
        }

        private void WriteDailySnapshot(int absoluteDay, String currentDate)
        {
            Population pop = EntityManager.GetComponentData<Population>(m_CitySystem.City);

            // trade by resource
            var tradeByResource = new Dictionary<string, int>();
            foreach (var resource in kTradeResources)
            {
                int idx = EconomyUtils.GetResourceIndex(resource);
                tradeByResource[resource.ToString()] = m_CityStatisticsSystem.GetStatisticValue(StatisticType.Trade, idx);
            }

            m_TaxSystem.Update();
            Unity.Collections.NativeArray<int> taxRates = m_TaxSystem.GetTaxRates();
            
            var residentialTaxes = new List<int>();
            for (int lvl = 0; lvl <= 4; lvl++) {
                residentialTaxes.Add(taxRates[(int)TaxAreaType.Residential] + taxRates[5 + lvl]);
            }

            var commercialTaxes = new Dictionary<string, int>();
            var industrialTaxes = new Dictionary<string, int>();
            var officeTaxes = new Dictionary<string, int>();

            foreach (var resource in kTradeResources)
            {
                int resourceIndex = EconomyUtils.GetResourceIndex(resource);
                if (resourceIndex != -1)
                {
                    string resourceName = resource.ToString().ToLower();
                    commercialTaxes[resourceName] = taxRates[(int)TaxAreaType.Commercial] + taxRates[10 + resourceIndex];
                    industrialTaxes[resourceName] = taxRates[(int)TaxAreaType.Industrial] + taxRates[10 + resourceIndex];
                    officeTaxes[resourceName]     = taxRates[(int)TaxAreaType.Office]     + taxRates[10 + resourceIndex];
                }
            }

            var snapshot = new
            {
                type = "daily",
                city_name = m_CityConfigurationSystem.cityName,
                date = currentDate,
                // Demographics
                children = m_CityStatisticsSystem.GetStatisticValue(StatisticType.Age, 0),
                teens    = m_CityStatisticsSystem.GetStatisticValue(StatisticType.Age, 1),
                adults   = m_CityStatisticsSystem.GetStatisticValue(StatisticType.Age, 2),
                seniors  = m_CityStatisticsSystem.GetStatisticValue(StatisticType.Age, 3),
                households = m_CityStatisticsSystem.GetStatisticValue(StatisticType.HouseholdCount),
                household_wealth = m_CityStatisticsSystem.GetStatisticValue(StatisticType.HouseholdWealth), // If you divide household wealth by # households you get the average household "bank balance"
                homeless = m_CityStatisticsSystem.GetStatisticValue(StatisticType.HomelessCount),
                birth_rate = m_CityStatisticsSystem.GetStatisticValue(StatisticType.BirthRate),
                death_rate = m_CityStatisticsSystem.GetStatisticValue(StatisticType.DeathRate),

                // Education
                education_uneducated = m_CityStatisticsSystem.GetStatisticValue(StatisticType.EducationCount, 0),
                education_poorly_educated = m_CityStatisticsSystem.GetStatisticValue(StatisticType.EducationCount, 1),
                education_educated = m_CityStatisticsSystem.GetStatisticValue(StatisticType.EducationCount, 2),
                education_well_educated = m_CityStatisticsSystem.GetStatisticValue(StatisticType.EducationCount, 3),
                education_highly_educated = m_CityStatisticsSystem.GetStatisticValue(StatisticType.EducationCount, 4),

                // Wellbeing
                wellbeing = m_CityStatisticsSystem.GetStatisticValue(StatisticType.Wellbeing),
                health = m_CityStatisticsSystem.GetStatisticValue(StatisticType.Health),
                 
                // Crime
                crime_count = m_CityStatisticsSystem.GetStatisticValue(StatisticType.CrimeCount),
                crime_rate = m_CityStatisticsSystem.GetStatisticValue(StatisticType.CrimeRate),
                escaped_arrests = m_CityStatisticsSystem.GetStatisticValue(StatisticType.EscapedArrestCount),

                // Economy
                balance = m_CityStatisticsSystem.GetStatisticValue(StatisticType.Money),
                income_tax_residential   = m_CityServiceBudgetSystem.GetIncome(IncomeSource.TaxResidential),
                income_tax_commercial    = m_CityServiceBudgetSystem.GetIncome(IncomeSource.TaxCommercial),
                income_tax_industrial    = m_CityServiceBudgetSystem.GetIncome(IncomeSource.TaxIndustrial),
                income_tax_office        = m_CityServiceBudgetSystem.GetIncome(IncomeSource.TaxOffice),
                income_export_electricity = m_CityServiceBudgetSystem.GetIncome(IncomeSource.ExportElectricity),
                income_export_water      = m_CityServiceBudgetSystem.GetIncome(IncomeSource.ExportWater),
                income_fee_education     = m_CityServiceBudgetSystem.GetIncome(IncomeSource.FeeEducation),
                income_fee_healthcare    = m_CityServiceBudgetSystem.GetIncome(IncomeSource.FeeHealthcare),
                income_fee_parking       = m_CityServiceBudgetSystem.GetIncome(IncomeSource.FeeParking),
                income_fee_transport     = m_CityServiceBudgetSystem.GetIncome(IncomeSource.FeePublicTransport),
                income_fee_garbage       = m_CityServiceBudgetSystem.GetIncome(IncomeSource.FeeGarbage),
                income_fee_electricity   = m_CityServiceBudgetSystem.GetIncome(IncomeSource.FeeElectricity),
                income_fee_water         = m_CityServiceBudgetSystem.GetIncome(IncomeSource.FeeWater),
                income_government_subsidy = m_CityServiceBudgetSystem.GetIncome(IncomeSource.GovernmentSubsidy),

                // Expenses by source
                expense_service_upkeep        = m_CityServiceBudgetSystem.GetExpense(ExpenseSource.ServiceUpkeep),
                expense_loan_interest         = m_CityServiceBudgetSystem.GetExpense(ExpenseSource.LoanInterest),
                expense_import_electricity    = m_CityServiceBudgetSystem.GetExpense(ExpenseSource.ImportElectricity),
                expense_import_water          = m_CityServiceBudgetSystem.GetExpense(ExpenseSource.ImportWater),
                expense_export_sewage         = m_CityServiceBudgetSystem.GetExpense(ExpenseSource.ExportSewage),
                expense_subsidy_commercial    = m_CityServiceBudgetSystem.GetExpense(ExpenseSource.SubsidyCommercial),
                expense_subsidy_industrial    = m_CityServiceBudgetSystem.GetExpense(ExpenseSource.SubsidyIndustrial),
                expense_subsidy_office        = m_CityServiceBudgetSystem.GetExpense(ExpenseSource.SubsidyOffice),
                expense_subsidy_residential   = m_CityServiceBudgetSystem.GetExpense(ExpenseSource.SubsidyResidential),
                expense_import_police         = m_CityServiceBudgetSystem.GetExpense(ExpenseSource.ImportPoliceService),
                expense_import_ambulance      = m_CityServiceBudgetSystem.GetExpense(ExpenseSource.ImportAmbulanceService),
                expense_import_garbage        = m_CityServiceBudgetSystem.GetExpense(ExpenseSource.ImportGarbageService),
                expense_import_hearse         = m_CityServiceBudgetSystem.GetExpense(ExpenseSource.ImportHearseService),
                expense_import_fire           = m_CityServiceBudgetSystem.GetExpense(ExpenseSource.ImportFireEngineService),
                expense_map_tile_upkeep       = m_CityServiceBudgetSystem.GetExpense(ExpenseSource.MapTileUpkeep),

                tourist_income = m_CityStatisticsSystem.GetStatisticValue(StatisticType.TouristIncome),
                tourists = m_CityStatisticsSystem.GetStatisticValue(StatisticType.TouristCount),
                lodging_total = m_CityStatisticsSystem.GetStatisticValue(StatisticType.LodgingTotal),
                lodging_used = m_CityStatisticsSystem.GetStatisticValue(StatisticType.LodgingUsed),
                trade_by_resource = tradeByResource,
                
                // Taxable income by sector
                residential_taxable_income = m_CityStatisticsSystem.GetStatisticValue(StatisticType.ResidentialTaxableIncome),
                commercial_taxable_income = m_CityStatisticsSystem.GetStatisticValue(StatisticType.CommercialTaxableIncome),
                industrial_taxable_income = m_CityStatisticsSystem.GetStatisticValue(StatisticType.IndustrialTaxableIncome),
                office_taxable_income = m_CityStatisticsSystem.GetStatisticValue(StatisticType.OfficeTaxableIncome),
                tax_rates_residential = residentialTaxes,
                tax_rates_commercial  = commercialTaxes,
                tax_rates_industrial  = industrialTaxes,
                tax_rates_office      = officeTaxes,

                // Employment
                workers = m_CityStatisticsSystem.GetStatisticValue(StatisticType.WorkerCount),
                unemployed = m_CityStatisticsSystem.GetStatisticValue(StatisticType.Unemployed),
                senior_worker_demand_pct = m_CityStatisticsSystem.GetStatisticValue(StatisticType.SeniorWorkerInDemandPercentage),
                city_service_workers = m_CityStatisticsSystem.GetStatisticValue(StatisticType.CityServiceWorkers),
                city_service_max_workers = m_CityStatisticsSystem.GetStatisticValue(StatisticType.CityServiceMaxWorkers),
                processing_workers = m_CityStatisticsSystem.GetStatisticValue(StatisticType.ProcessingWorkers),
                processing_max_workers = m_CityStatisticsSystem.GetStatisticValue(StatisticType.ProcessingMaxWorkers),
                processing_count = m_CityStatisticsSystem.GetStatisticValue(StatisticType.ProcessingCount),
                processing_wealth = m_CityStatisticsSystem.GetStatisticValue(StatisticType.ProcessingWealth),
                service_count = m_CityStatisticsSystem.GetStatisticValue(StatisticType.ServiceCount),
                service_max_workers = m_CityStatisticsSystem.GetStatisticValue(StatisticType.ServiceMaxWorkers),
                service_wealth = m_CityStatisticsSystem.GetStatisticValue(StatisticType.ServiceWealth),

                // Mail
                collected_mail = m_CityStatisticsSystem.GetStatisticValue(StatisticType.CollectedMail),
                delivered_mail = m_CityStatisticsSystem.GetStatisticValue(StatisticType.DeliveredMail),

                // Transport - passengers
                passengers_bus = m_CityStatisticsSystem.GetStatisticValue(StatisticType.PassengerCountBus),
                passengers_tram = m_CityStatisticsSystem.GetStatisticValue(StatisticType.PassengerCountTram),
                passengers_subway = m_CityStatisticsSystem.GetStatisticValue(StatisticType.PassengerCountSubway),
                passengers_train = m_CityStatisticsSystem.GetStatisticValue(StatisticType.PassengerCountTrain),
                passengers_ship = m_CityStatisticsSystem.GetStatisticValue(StatisticType.PassengerCountShip),
                passengers_ferry = m_CityStatisticsSystem.GetStatisticValue(StatisticType.PassengerCountFerry),
                passengers_airplane = m_CityStatisticsSystem.GetStatisticValue(StatisticType.PassengerCountAirplane),
                passengers_taxi = m_CityStatisticsSystem.GetStatisticValue(StatisticType.PassengerCountTaxi),

                // Transport - cargo
                cargo_train = m_CityStatisticsSystem.GetStatisticValue(StatisticType.CargoCountTrain),
                cargo_ship = m_CityStatisticsSystem.GetStatisticValue(StatisticType.CargoCountShip),
                cargo_airplane = m_CityStatisticsSystem.GetStatisticValue(StatisticType.CargoCountAirplane),
                cargo_truck = m_CityStatisticsSystem.GetStatisticValue(StatisticType.CargoCountTruck),
            };
            Publish("telex/daily", snapshot);
        }

        private void WriteGraphSnapshot(int absoluteDay, String currentDate)
        {
            var edges = m_EdgeQuery.ToEntityArray(Allocator.Temp);
            var records = new List<object>(edges.Length);

            foreach (var edgeEntity in edges)
            {
                var edge = EntityManager.GetComponentData<Game.Net.Edge>(edgeEntity);
                var curve = EntityManager.GetComponentData<Game.Net.Curve>(edgeEntity);
                var startNode = EntityManager.GetComponentData<Game.Net.Node>(edge.m_Start);
                var endNode = EntityManager.GetComponentData<Game.Net.Node>(edge.m_End);

                float2 elevation = default;
                if (EntityManager.HasComponent<Game.Net.Elevation>(edgeEntity))
                    elevation = EntityManager.GetComponentData<Game.Net.Elevation>(edgeEntity).m_Elevation;

                var coverageList = new List<object>();
                if (EntityManager.HasBuffer<Game.Net.ServiceCoverage>(edgeEntity))
                {
                    var coverage = EntityManager.GetBuffer<Game.Net.ServiceCoverage>(edgeEntity);
                    for (int i = 0; i < coverage.Length; i++)
                    {
                        coverageList.Add(new
                        {
                            service = i,
                            min = coverage[i].m_Coverage.x,
                            max = coverage[i].m_Coverage.y
                        });
                    }
                }

                // Road-level traffic data (present on road edges, not all edge types)
                object trafficRecord = null;
                if (EntityManager.HasComponent<Game.Net.Road>(edgeEntity))
                {
                    var road = EntityManager.GetComponentData<Game.Net.Road>(edgeEntity);
                    // Speed: distance/duration per lane group, expressed as float4 (4 carriageway groups)
                    // Volume proxy: sqrt(distance * 5.333) matches game infoview formula
                    var dur0 = road.m_TrafficFlowDuration0;
                    var dur1 = road.m_TrafficFlowDuration1;
                    var dist0 = road.m_TrafficFlowDistance0;
                    var dist1 = road.m_TrafficFlowDistance1;
                    float4 speed0 = math.select(0f, dist0 / dur0, dur0 > 0f);
                    float4 speed1 = math.select(0f, dist1 / dur1, dur1 > 0f);
                    float4 vol0 = math.sqrt((dist0 + dist1) * 2.6666667f);
                    trafficRecord = new
                    {
                        // direction 0 (forward lanes) per carriageway group
                        speed0 = new float[] { speed0.x, speed0.y, speed0.z, speed0.w },
                        speed1 = new float[] { speed1.x, speed1.y, speed1.z, speed1.w },
                        // combined volume proxy (matches game TrafficVolume infoview)
                        volume = new float[] { vol0.x, vol0.y, vol0.z, vol0.w },
                        // raw accumulators for downstream recomputation
                        duration0 = new float[] { dur0.x, dur0.y, dur0.z, dur0.w },
                        duration1 = new float[] { dur1.x, dur1.y, dur1.z, dur1.w },
                        distance0 = new float[] { dist0.x, dist0.y, dist0.z, dist0.w },
                        distance1 = new float[] { dist1.x, dist1.y, dist1.z, dist1.w },
                    };
                }

                records.Add(new
                {
                    entity = edgeEntity.Index,
                    version = edgeEntity.Version,
                    start_entity = edge.m_Start.Index,
                    end_entity = edge.m_End.Index,
                    start_pos = new { x = startNode.m_Position.x, y = startNode.m_Position.y, z = startNode.m_Position.z },
                    end_pos = new { x = endNode.m_Position.x, y = endNode.m_Position.y, z = endNode.m_Position.z },
                    elevation = new { min = elevation.x, max = elevation.y },
                    curve_a = new { x = curve.m_Bezier.a.x, y = curve.m_Bezier.a.y, z = curve.m_Bezier.a.z },
                    curve_b = new { x = curve.m_Bezier.b.x, y = curve.m_Bezier.b.y, z = curve.m_Bezier.b.z },
                    curve_c = new { x = curve.m_Bezier.c.x, y = curve.m_Bezier.c.y, z = curve.m_Bezier.c.z },
                    curve_d = new { x = curve.m_Bezier.d.x, y = curve.m_Bezier.d.y, z = curve.m_Bezier.d.z },
                    service_coverage = coverageList,
                    traffic = trafficRecord
                });
            }

            var graphSnapshot = new
            {
                type = "graph",
                city_name = m_CityConfigurationSystem.cityName,
                day = absoluteDay,
                current_date = currentDate,
                edges = records
            };
            Publish("telex/graph", graphSnapshot);
            edges.Dispose();
        }

        private void WriteTrafficSnapshot(int absoluteDay, string currentDate)
        {
            // Per-lane flow data. Each CarLane entity with LaneFlow is a sublane of a road edge.
            // EdgeLane.m_EdgeDelta tells us where on the edge this lane sits (0=start, 1=end).
            // Lane.m_StartNode / m_EndNode carry the owner edge index via PathNode.
            var lanes = m_TrafficQuery.ToEntityArray(Allocator.Temp);
            var records = new List<object>(lanes.Length);

            var ownerLookup = GetComponentLookup<Game.Common.Owner>(true);

            foreach (var laneEntity in lanes)
            {
                var flow = EntityManager.GetComponentData<Game.Net.LaneFlow>(laneEntity);
                var edgeLane = EntityManager.GetComponentData<Game.Net.EdgeLane>(laneEntity);
                var lane = EntityManager.GetComponentData<Game.Net.Lane>(laneEntity);
                var carLane = EntityManager.GetComponentData<Game.Net.CarLane>(laneEntity);

                // Compute average speed for this lane (same formula as NetUtils.GetTrafficFlowSpeed)
                float4 dur = flow.m_Duration;
                float4 dist = flow.m_Distance;
                float4 speed = math.select(0f, dist / dur, dur > 0f);

                // Resolve owner edge entity index
                int ownerEdgeIndex = -1;
                int ownerEdgeVersion = -1;
                if (ownerLookup.TryGetComponent(laneEntity, out var owner))
                {
                    ownerEdgeIndex = owner.m_Owner.Index;
                    ownerEdgeVersion = owner.m_Owner.Version;
                }

                records.Add(new
                {
                    entity = laneEntity.Index,
                    version = laneEntity.Version,
                    owner_edge = ownerEdgeIndex,
                    owner_edge_version = ownerEdgeVersion,
                    // EdgeDelta: x=position along edge at lane start, y=at lane end (0..1)
                    edge_delta_start = edgeLane.m_EdgeDelta.x,
                    edge_delta_end   = edgeLane.m_EdgeDelta.y,
                    // Carriageway group encodes direction + carriageway index
                    carriageway_group = carLane.m_CarriagewayGroup,
                    // Speed in m/s per lane group component (usually only .x is non-zero for a single lane)
                    speed = new float[] { speed.x, speed.y, speed.z, speed.w },
                    // Raw accumulators
                    duration = new float[] { dur.x, dur.y, dur.z, dur.w },
                    distance = new float[] { dist.x, dist.y, dist.z, dist.w },
                    // m_Next: fractional position of next vehicle ahead (for congestion detection)
                    next_x = flow.m_Next.x,
                    next_y = flow.m_Next.y,
                });
            }

            var snapshot = new
            {
                type = "traffic",
                city_name = m_CityConfigurationSystem.cityName,
                day = absoluteDay,
                current_date = currentDate,
                lanes = records
            };
            Publish("telex/traffic", snapshot);
            lanes.Dispose();
        }

        private void WriteDemandSnapshot(int absoluteDay, string currentDate)
        {
            var snapshot = new
            {
                type = "demand",
                city_name = m_CityConfigurationSystem.cityName,
                day = absoluteDay,
                current_date = currentDate,
                
                // Commercial
                commercial_company_demand = m_CommercialDemandSystem.companyDemand,
                commercial_building_demand = m_CommercialDemandSystem.buildingDemand,

                // Industrial
                industrial_company_demand = m_IndustrialDemandSystem.industrialCompanyDemand,
                industrial_building_demand = m_IndustrialDemandSystem.industrialBuildingDemand,
                storage_company_demand = m_IndustrialDemandSystem.storageCompanyDemand,
                storage_building_demand = m_IndustrialDemandSystem.storageBuildingDemand,
                office_company_demand = m_IndustrialDemandSystem.officeCompanyDemand,
                office_building_demand = m_IndustrialDemandSystem.officeBuildingDemand,

                // Residential
                household_demand = m_ResidentialDemandSystem.householdDemand,
                residential_building_demand_low = m_ResidentialDemandSystem.buildingDemand.x,
                residential_building_demand_medium = m_ResidentialDemandSystem.buildingDemand.y,
                residential_building_demand_high = m_ResidentialDemandSystem.buildingDemand.z,
            };

            Publish("telex/demand", snapshot);

        }


        private void WriteBuildingSnapshot(int absoluteDay, String currentDate)
        {
            var buildings = m_BuildingQuery.ToEntityArray(Allocator.Temp);
            var records = new List<object>(buildings.Length);
                        
            var schoolDataLookup = GetComponentLookup<Game.Prefabs.SchoolData>(true);
            var hospitalDataLookup = GetComponentLookup<Game.Prefabs.HospitalData>(true);
            var electricityLookup = GetComponentLookup<Game.Buildings.ElectricityConsumer>(true);
            var waterLookup = GetComponentLookup<Game.Buildings.WaterConsumer>(true);
            var spawnableLookup = GetComponentLookup<Game.Prefabs.SpawnableBuildingData>(true);
            var commercialLookup = GetComponentLookup<Game.Buildings.CommercialProperty>(true);
            var industrialLookup = GetComponentLookup<Game.Buildings.IndustrialProperty>(true);
            var serviceAvailableLookup = GetComponentLookup<Game.Companies.ServiceAvailable>(true);
            var efficiencyLookup = GetComponentLookup<Game.Buildings.BuildingEfficiency>(true);
            var residentialLookup = GetComponentLookup<Game.Buildings.ResidentialProperty>(true);
            var officeLookup = GetComponentLookup<Game.Buildings.OfficeProperty>(true);

            foreach (var buildingEntity in buildings)
            {
                var building = EntityManager.GetComponentData<Game.Buildings.Building>(buildingEntity);
                var transform = EntityManager.GetComponentData<Game.Objects.Transform>(buildingEntity);
                var prefabRef = EntityManager.GetComponentData<Game.Prefabs.PrefabRef>(buildingEntity);
                Entity prefab = prefabRef.m_Prefab;
                
                bool hasElectricity = electricityLookup.HasComponent(buildingEntity);
                bool hasWater = waterLookup.HasComponent(buildingEntity);
                bool isResidential = residentialLookup.HasComponent(buildingEntity);
                bool isCommercial = commercialLookup.HasComponent(buildingEntity);
                bool isIndustrial = industrialLookup.HasComponent(buildingEntity);
                bool isOffice = officeLookup.HasComponent(buildingEntity);
                bool hasService = serviceAvailableLookup.HasComponent(buildingEntity);

                // zone type string
                string zoneType = isResidential ? "residential"
                    : isCommercial ? "commercial"
                    : isIndustrial ? "industrial"
                    : isOffice ? "office"
                    : "other";

                bool hasBuildingData = EntityManager.HasComponent<Game.Prefabs.BuildingData>(prefab);
                Game.Prefabs.BuildingData prefabBuildingData = hasBuildingData
                    ? EntityManager.GetComponentData<Game.Prefabs.BuildingData>(prefab)
                    : default;

                object schoolRecord = null;
                if (schoolDataLookup.HasComponent(prefab))
                {
                    var school = schoolDataLookup[prefab];
                    schoolRecord = new
                    {
                        student_capacity = school.m_StudentCapacity,
                        education_level = (int)school.m_EducationLevel,
                        graduation_modifier = school.m_GraduationModifier,
                        student_wellbeing = (int)school.m_StudentWellbeing,
                        student_health = (int)school.m_StudentHealth
                    };
                }

                object hospitalRecord = null;
                if (hospitalDataLookup.HasComponent(prefab))
                {
                    var hospital = hospitalDataLookup[prefab];
                    hospitalRecord = new
                    {
                        patient_capacity = hospital.m_PatientCapacity,
                        ambulance_capacity = hospital.m_AmbulanceCapacity,
                        helicopter_capacity = hospital.m_MedicalHelicopterCapacity,
                        treatment_bonus = hospital.m_TreatmentBonus,
                        health_range_min = hospital.m_HealthRange.x,
                        health_range_max = hospital.m_HealthRange.y
                    };
                }

                records.Add(new
                {
                    entity = buildingEntity.Index,
                    version = buildingEntity.Version,
                    zone_type = zoneType,
                    commercial_resources = isCommercial ? commercialLookup[buildingEntity].m_Resources.ToString() : null,
                    industrial_resources = isIndustrial ? industrialLookup[buildingEntity].m_Resources.ToString() : null,
                    pos = new { x = transform.m_Position.x, y = transform.m_Position.y, z = transform.m_Position.z },
                    road_edge = building.m_RoadEdge.Index,
                    curve_position = building.m_CurvePosition,
                    has_water_node = hasBuildingData && (prefabBuildingData.m_Flags & Game.Prefabs.BuildingFlags.HasWaterNode) != 0,
                    has_electricity_node = hasBuildingData && (prefabBuildingData.m_Flags & Game.Prefabs.BuildingFlags.HasLowVoltageNode) != 0,
                    has_sewage_node = hasBuildingData && (prefabBuildingData.m_Flags & Game.Prefabs.BuildingFlags.HasSewageNode) != 0,
                    service_available = hasService ? serviceAvailableLookup[buildingEntity].m_ServiceAvailable : 0,
                    service_mean_priority = hasService ? serviceAvailableLookup[buildingEntity].m_MeanPriority : 0f,
                    electricity = hasElectricity ? new
                    {
                        wanted = electricityLookup[buildingEntity].m_WantedConsumption,
                        fulfilled = electricityLookup[buildingEntity].m_FulfilledConsumption,
                        connected = electricityLookup[buildingEntity].electricityConnected
                    } : null,
                    water = hasWater ? new
                    {
                        wanted = waterLookup[buildingEntity].m_WantedConsumption,
                        fulfilled_fresh = waterLookup[buildingEntity].m_FulfilledFresh,
                        fulfilled_sewage = waterLookup[buildingEntity].m_FulfilledSewage,
                        pollution = waterLookup[buildingEntity].m_Pollution,
                        water_connected = waterLookup[buildingEntity].waterConnected,
                        sewage_connected = waterLookup[buildingEntity].sewageConnected
                    } : null,
                    school = schoolRecord,
                    hospital = hospitalRecord
                });
            }

            var snapshot = new
            {
                type = "buildings",
                city_name = m_CityConfigurationSystem.cityName,
                day = absoluteDay,
                current_date = currentDate,
                buildings = records
            };

            Publish("telex/buildings", snapshot);
            buildings.Dispose();
        }
    }
}
