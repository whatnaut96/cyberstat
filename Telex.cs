using System;
using Colossal.Logging;
using Game;
using Game.Areas;
using Game.Buildings;
using Game.Citizens;
using Game.City;
using Game.Common;
using Game.Companies;
using Game.Economy;
using Game.Modding;
using Game.Simulation;
using Game.Tools;
using Game.Routes;
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
        //private NameSystem m_NameSystem;
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
        private EntityQuery m_CitizenQuery;
        private EntityQuery m_DistrictQuery;
        private EntityQuery m_TransportLineQuery;
        private EntityQuery m_TransportStopQuery;

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
            //m_NameSystem = World.GetOrCreateSystemManaged<NameSystem>();
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
            m_CitizenQuery = GetEntityQuery(ComponentType.ReadOnly<Citizen>());

            m_DistrictQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Areas.District>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>()
            );

            m_TransportLineQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Routes.Route>(),
                ComponentType.ReadOnly<Game.Routes.TransportLine>(),
                ComponentType.ReadOnly<Game.Prefabs.PrefabRef>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>()
            );

            m_TransportStopQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Routes.TransportStop>(),
                ComponentType.ReadOnly<Game.Common.Owner>(),
                ComponentType.ReadOnly<Game.Prefabs.PrefabRef>(),
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

            WriteFactSnapshot(absoluteDay, dateString);
            WriteEconomicSnapshot(absoluteDay, dateString);
            WriteCargoSnapshot(absoluteDay, dateString);
            WriteCrimeSnapshot(absoluteDay, dateString);
            WriteGraphSnapshot(absoluteDay, dateString);
            WriteTrafficSnapshot(absoluteDay, dateString);
            WriteDistrictSnapshot(absoluteDay, dateString);
            WriteTransportSnapshot(absoluteDay, dateString);
            WriteCitizenSnapshot(absoluteDay, dateString);
            WriteBuildingSnapshot(absoluteDay, dateString);
        }

        public static int GetTaxRateForResource(TaxAreaType areaType, int rawResourceInt, NativeArray<int> taxRates)
        {
            Resource resourceEnum = (Resource)rawResourceInt;

            int resourceIndex = EconomyUtils.GetResourceIndex(resourceEnum);
            if (resourceIndex == -1) return 0;

            return areaType switch
            {
                TaxAreaType.Residential => taxRates[(int)TaxAreaType.Residential] + taxRates[5 + rawResourceInt], // jobLevel directly
                TaxAreaType.Commercial => taxRates[(int)TaxAreaType.Commercial] + taxRates[10 + resourceIndex],
                TaxAreaType.Industrial => taxRates[(int)TaxAreaType.Industrial] + taxRates[10 + resourceIndex],
                TaxAreaType.Office => taxRates[(int)TaxAreaType.Office] + taxRates[10 + resourceIndex],
                _ => 0
            };
        }

        private void WriteEconomicSnapshot(int absoluteDay, string CurrentDate) 
        {
            var economySnapshot = new 
            {
                balance = m_CityStatisticsSystem.GetStatisticValue(StatisticType.Money),
                income_tax_residential = m_CityServiceBudgetSystem.GetIncome(IncomeSource.TaxResidential),
                income_tax_commercial = m_CityServiceBudgetSystem.GetIncome(IncomeSource.TaxCommercial),
                income_tax_industrial = m_CityServiceBudgetSystem.GetIncome(IncomeSource.TaxIndustrial),
                income_tax_office = m_CityServiceBudgetSystem.GetIncome(IncomeSource.TaxOffice),
                income_export_electricity = m_CityServiceBudgetSystem.GetIncome(IncomeSource.ExportElectricity),
                income_export_water = m_CityServiceBudgetSystem.GetIncome(IncomeSource.ExportWater),
                income_fee_education = m_CityServiceBudgetSystem.GetIncome(IncomeSource.FeeEducation),
                income_fee_healthcare = m_CityServiceBudgetSystem.GetIncome(IncomeSource.FeeHealthcare),
                income_fee_parking = m_CityServiceBudgetSystem.GetIncome(IncomeSource.FeeParking),
                income_fee_transport = m_CityServiceBudgetSystem.GetIncome(IncomeSource.FeePublicTransport),
                income_fee_garbage = m_CityServiceBudgetSystem.GetIncome(IncomeSource.FeeGarbage),
                income_fee_electricity = m_CityServiceBudgetSystem.GetIncome(IncomeSource.FeeElectricity),
                income_fee_water = m_CityServiceBudgetSystem.GetIncome(IncomeSource.FeeWater),
                income_government_subsidy = m_CityServiceBudgetSystem.GetIncome(IncomeSource.GovernmentSubsidy),
                expense_service_upkeep = m_CityServiceBudgetSystem.GetExpense(ExpenseSource.ServiceUpkeep),
                expense_loan_interest = m_CityServiceBudgetSystem.GetExpense(ExpenseSource.LoanInterest),
                expense_import_electricity = m_CityServiceBudgetSystem.GetExpense(ExpenseSource.ImportElectricity),
                expense_import_water = m_CityServiceBudgetSystem.GetExpense(ExpenseSource.ImportWater),
                expense_export_sewage = m_CityServiceBudgetSystem.GetExpense(ExpenseSource.ExportSewage),
                expense_subsidy_commercial = m_CityServiceBudgetSystem.GetExpense(ExpenseSource.SubsidyCommercial),
                expense_subsidy_industrial = m_CityServiceBudgetSystem.GetExpense(ExpenseSource.SubsidyIndustrial),
                expense_subsidy_office = m_CityServiceBudgetSystem.GetExpense(ExpenseSource.SubsidyOffice),
                expense_subsidy_residential = m_CityServiceBudgetSystem.GetExpense(ExpenseSource.SubsidyResidential),
                expense_import_police = m_CityServiceBudgetSystem.GetExpense(ExpenseSource.ImportPoliceService),
                expense_import_ambulance = m_CityServiceBudgetSystem.GetExpense(ExpenseSource.ImportAmbulanceService),
                expense_import_garbage = m_CityServiceBudgetSystem.GetExpense(ExpenseSource.ImportGarbageService),
                expense_import_hearse = m_CityServiceBudgetSystem.GetExpense(ExpenseSource.ImportHearseService),
                expense_import_fire = m_CityServiceBudgetSystem.GetExpense(ExpenseSource.ImportFireEngineService),
                expense_map_tile_upkeep = m_CityServiceBudgetSystem.GetExpense(ExpenseSource.MapTileUpkeep),

                tourist_income = m_CityStatisticsSystem.GetStatisticValue(StatisticType.TouristIncome),
                residential_taxable_income = m_CityStatisticsSystem.GetStatisticValue(StatisticType.ResidentialTaxableIncome),
                commercial_taxable_income = m_CityStatisticsSystem.GetStatisticValue(StatisticType.CommercialTaxableIncome),
                industrial_taxable_income = m_CityStatisticsSystem.GetStatisticValue(StatisticType.IndustrialTaxableIncome),
                office_taxable_income = m_CityStatisticsSystem.GetStatisticValue(StatisticType.OfficeTaxableIncome),
            };

            Publish("telex/economy", economySnapshot);
        }

        private void WriteCrimeSnapshot(int absoluteDay, string currentDate)
        {
            var crimeSnapshot = new {
                crime_count = m_CityStatisticsSystem.GetStatisticValue(StatisticType.CrimeCount),
                crime_rate = m_CityStatisticsSystem.GetStatisticValue(StatisticType.CrimeRate),
                escaped_arrests = m_CityStatisticsSystem.GetStatisticValue(StatisticType.EscapedArrestCount),
            };

            Publish("telex/crime", crimeSnapshot);
        }

        private void WriteCargoSnapshot() {
            var cargoSnapshot = new {
                cargo_train = m_CityStatisticsSystem.GetStatisticValue(StatisticType.CargoCountTrain),
                cargo_ship = m_CityStatisticsSystem.GetStatisticValue(StatisticType.CargoCountShip),
                cargo_airplane = m_CityStatisticsSystem.GetStatisticValue(StatisticType.CargoCountAirplane),
                cargo_truck = m_CityStatisticsSystem.GetStatisticValue(StatisticType.CargoCountTruck),
            };
            Publish("telex/cargo", cargoSnapshot);
        }

        private void WriteTradeSnapshot(int absoluteDay, String currentDate)
        {
            var tradeByResource = new Dictionary<string, int>();
            var residentialTaxes = new List<int>();
            for (int lvl = 0; lvl <= 4; lvl++)
            {
                residentialTaxes.Add(taxRates[(int)TaxAreaType.Residential] + taxRates[5 + lvl]);
            }

            var commercialTaxes = new Dictionary<string, int>();
            var industrialTaxes = new Dictionary<string, int>();
            var officeTaxes = new Dictionary<string, int>();

            m_TaxSystem.Update();
            Unity.Collections.NativeArray<int> taxRates = m_TaxSystem.GetTaxRates();
            foreach (var resource in kTradeResources)
            {
                if (resourceIndex != -1) {
                    int idx = EconomyUtils.GetResourceIndex(resource);
                    tradeByResource[resource.ToString()] = m_CityStatisticsSystem.GetStatisticValue(StatisticType.Trade, idx);
                    string resourceName = resource.ToString().ToLower();
                    commercialTaxes[resourceName] = taxRates[(int)TaxAreaType.Commercial] + taxRates[10 + idx];
                    industrialTaxes[resourceName] = taxRates[(int)TaxAreaType.Industrial] + taxRates[10 + idx];
                    officeTaxes[resourceName] = taxRates[(int)TaxAreaType.Office] + taxRates[10 + idx];
                }
            }

            var taxRateSnapshot = new {
                tax_rates_residential = residentialTaxes,
                tax_rates_commercial = commercialTaxes,
                tax_rates_industrial = industrialTaxes,
                tax_rates_office = officeTaxes,
            };
            var tradeSnapshot = new {
                trade_by_resource = tradeByResource
            };
            Publish("telex/trade", tradeSnapshot);
            Publish("telex/tax_rate", taxRateSnapshot);
        }

        private void WriteTourismSnapshot(int absoluteDay, string currentDate)
        {

                var tourismSnapshot = new {
                    tourists = m_CityStatisticsSystem.GetStatisticValue(StatisticType.TouristCount),
                    lodging_total = m_CityStatisticsSystem.GetStatisticValue(StatisticType.LodgingTotal),
                    lodging_used = m_CityStatisticsSystem.GetStatisticValue(StatisticType.LodgingUsed)
                };

                Publish("telex/tourism", tourismSnapshot)
        }

        private void WriteLaborSnapshot(int absoluteDay, String currentDate)
        {
            var laborSnapshot = new {
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
            };
            
            Publish("telex/labor", laborSnapshot);
        }


        private void WriteMailSnapshot(int absoluteDay, string currentDate)
        {

            var mailSnapshot = new {
                collected_mail = m_CityStatisticsSystem.GetStatisticValue(StatisticType.CollectedMail),
                delivered_mail = m_CityStatisticsSystem.GetStatisticValue(StatisticType.DeliveredMail),
            };
            Publish("telex/mail", mailSnapshot);
        }

        private void WriteFactSnapshot(int absoluteDay, String currentDate)
        {
            var snapshot = new
            {
                city_name = m_CityConfigurationSystem.cityName,
                date = currentDate,
            };
            Publish("telex/snapshot_meta", snapshot);
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
                    edge_delta_end = edgeLane.m_EdgeDelta.y,
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


        private object MakeEntityRef(Entity entity)
        {
            return entity == Entity.Null ? null : new { entity = entity.Index, version = entity.Version };
        }

        private int? GetDistrictId(Entity entity)
        {
            if (entity != Entity.Null && EntityManager.HasComponent<Game.Areas.CurrentDistrict>(entity))
            {
                var district = EntityManager.GetComponentData<Game.Areas.CurrentDistrict>(entity).m_District;
                if (district != Entity.Null) return district.Index;
            }
            return null;
        }

        private void WriteDistrictSnapshot(int absoluteDay, string currentDate)
        {
            var districts = m_DistrictQuery.ToEntityArray(Allocator.Temp);
            var records = new List<object>(districts.Length);

            Game.UI.NameSystem activeNameSystem = null;
            try { activeNameSystem = base.World.GetExistingSystemManaged<Game.UI.NameSystem>(); }
            catch { }

            foreach (var districtEntity in districts)
            {
                var district = EntityManager.GetComponentData<Game.Areas.District>(districtEntity);
                string districtName = null;
                if (activeNameSystem != null)
                {
                    if (!activeNameSystem.TryGetCustomName(districtEntity, out districtName))
                    {
                        districtName = activeNameSystem.GetRenderedLabelName(districtEntity);
                    }
                }
                if (string.IsNullOrEmpty(districtName)) districtName = $"District {districtEntity.Index}";

                records.Add(new
                {
                    district_id = districtEntity.Index,
                    district_version = districtEntity.Version,
                    name = districtName,
                    option_mask = district.m_OptionMask
                });
            }

            Publish("telex/dim/districts", new
            {
                type = "districts",
                city_name = m_CityConfigurationSystem.cityName,
                day = absoluteDay,
                current_date = currentDate,
                districts = records
            });
            districts.Dispose();
        }

        private void WriteTransportSnapshot(int absoluteDay, string currentDate)
        {
            var lineEntities = m_TransportLineQuery.ToEntityArray(Allocator.Temp);
            var stopEntities = m_TransportStopQuery.ToEntityArray(Allocator.Temp);

            var lines = new List<object>(lineEntities.Length);
            var stops = new List<object>(stopEntities.Length);
            var lineStops = new List<object>();
            var lineSegments = new List<object>();
            var lineVehicles = new List<object>();

            var prefabTransportLineData = GetComponentLookup<Game.Prefabs.TransportLineData>(true);
            var prefabTransportStopData = GetComponentLookup<Game.Prefabs.TransportStopData>(true);
            var routeInfoLookup = GetComponentLookup<Game.Routes.RouteInfo>(true);
            var routeNumberLookup = GetComponentLookup<Game.Routes.RouteNumber>(true);
            var colorLookup = GetComponentLookup<Game.Routes.Color>(true);
            var positionLookup = GetComponentLookup<Game.Routes.Position>(true);
            var waitingPassengersLookup = GetComponentLookup<Game.Routes.WaitingPassengers>(true);
            var routeLaneLookup = GetComponentLookup<Game.Routes.RouteLane>(true);
            var ownerLookup = GetComponentLookup<Game.Common.Owner>(true);
            var connectedLookup = GetComponentLookup<Game.Routes.Connected>(true);

            foreach (var lineEntity in lineEntities)
            {
                var route = EntityManager.GetComponentData<Game.Routes.Route>(lineEntity);
                var line = EntityManager.GetComponentData<Game.Routes.TransportLine>(lineEntity);
                var prefabRef = EntityManager.GetComponentData<Game.Prefabs.PrefabRef>(lineEntity);

                int? routeNumber = null;
                if (routeNumberLookup.HasComponent(lineEntity))
                    routeNumber = routeNumberLookup[lineEntity].m_Number;

                object routeInfo = null;
                if (routeInfoLookup.HasComponent(lineEntity))
                {
                    var info = routeInfoLookup[lineEntity];
                    routeInfo = new
                    {
                        duration = info.m_Duration,
                        distance = info.m_Distance,
                        flags = (int)info.m_Flags
                    };
                }

                object color = null;
                if (colorLookup.HasComponent(lineEntity))
                {
                    var c = colorLookup[lineEntity].m_Color;
                    color = new { r = c.r, g = c.g, b = c.b, a = c.a };
                }

                object transportData = null;
                if (prefabTransportLineData.HasComponent(prefabRef.m_Prefab))
                {
                    var data = prefabTransportLineData[prefabRef.m_Prefab];
                    transportData = new
                    {
                        transport_type = data.m_TransportType.ToString(),
                        default_vehicle_interval = data.m_DefaultVehicleInterval,
                        default_unbunching_factor = data.m_DefaultUnbunchingFactor,
                        stop_duration = data.m_StopDuration,
                        size_class = data.m_SizeClass.ToString(),
                        passenger_transport = data.m_PassengerTransport,
                        cargo_transport = data.m_CargoTransport
                    };
                }

                lines.Add(new
                {
                    route_id = lineEntity.Index,
                    route_version = lineEntity.Version,
                    route_number = routeNumber,
                    route_flags = (uint)route.m_Flags,
                    route_option_mask = route.m_OptionMask,
                    vehicle_request = MakeEntityRef(line.m_VehicleRequest),
                    vehicle_interval = line.m_VehicleInterval,
                    unbunching_factor = line.m_UnbunchingFactor,
                    line_flags = (ushort)line.m_Flags,
                    ticket_price = line.m_TicketPrice,
                    color = color,
                    route_info = routeInfo,
                    transport = transportData
                });

                if (EntityManager.HasBuffer<Game.Routes.RouteWaypoint>(lineEntity))
                {
                    var waypoints = EntityManager.GetBuffer<Game.Routes.RouteWaypoint>(lineEntity);

                    for (int i = 0; i < waypoints.Length; i++)
                    {
                        Entity waypointEntity = waypoints[i].m_Waypoint;

                        object waypointPos = null;
                        if (positionLookup.TryGetComponent(waypointEntity, out var waypointPosition))
                        {
                            var p = waypointPosition.m_Position;
                            waypointPos = new { x = p.x, y = p.y, z = p.z };
                        }

                        Entity connectedEntity = Entity.Null;
                        if (connectedLookup.TryGetComponent(waypointEntity, out var connected))
                        {
                            connectedEntity = connected.m_Connected;
                        }

                        object waiting = null;
                        if (waitingPassengersLookup.TryGetComponent(waypointEntity, out var w))
                        {
                            waiting = new
                            {
                                count = w.m_Count,
                                ongoing_accumulation = w.m_OngoingAccumulation,
                                concluded_accumulation = w.m_ConcludedAccumulation,
                                success_accumulation = w.m_SuccessAccumulation,
                                average_waiting_time = w.m_AverageWaitingTime
                            };
                        }

                        lineStops.Add(new
                        {
                            route_id = lineEntity.Index,
                            route_version = lineEntity.Version,
                            sequence = i,
                            waypoint_id = waypointEntity.Index,
                            waypoint_version = waypointEntity.Version,
                            position = waypointPos,
                            district_id = GetDistrictId(waypointEntity),
                            connected_entity = MakeEntityRef(connectedEntity),
                            connected_district_id = GetDistrictId(connectedEntity),
                            waiting_passengers = waiting
                        });
                    }
                }

                if (EntityManager.HasBuffer<Game.Routes.RouteSegment>(lineEntity))
                {
                    var segments = EntityManager.GetBuffer<Game.Routes.RouteSegment>(lineEntity);
                    for (int i = 0; i < segments.Length; i++)
                    {
                        lineSegments.Add(new
                        {
                            route_id = lineEntity.Index,
                            route_version = lineEntity.Version,
                            sequence = i,
                            segment_id = segments[i].m_Segment.Index,
                            segment_version = segments[i].m_Segment.Version
                        });
                    }
                }

                if (EntityManager.HasBuffer<Game.Routes.RouteVehicle>(lineEntity))
                {
                    var vehicles = EntityManager.GetBuffer<Game.Routes.RouteVehicle>(lineEntity);
                    for (int i = 0; i < vehicles.Length; i++)
                    {
                        lineVehicles.Add(new
                        {
                            route_id = lineEntity.Index,
                            route_version = lineEntity.Version,
                            vehicle_id = vehicles[i].m_Vehicle.Index,
                            vehicle_version = vehicles[i].m_Vehicle.Version
                        });
                    }
                }
            }

            foreach (var stopEntity in stopEntities)
            {
                var stop = EntityManager.GetComponentData<Game.Routes.TransportStop>(stopEntity);
                var owner = EntityManager.GetComponentData<Game.Common.Owner>(stopEntity);
                var prefabRef = EntityManager.GetComponentData<Game.Prefabs.PrefabRef>(stopEntity);

                object pos = null;
                if (positionLookup.TryGetComponent(stopEntity, out var stopPosition))
                {
                    var p = stopPosition.m_Position;
                    pos = new { x = p.x, y = p.y, z = p.z };
                }

                object waiting = null;
                if (waitingPassengersLookup.TryGetComponent(stopEntity, out var w))
                {
                    waiting = new
                    {
                        count = w.m_Count,
                        ongoing_accumulation = w.m_OngoingAccumulation,
                        concluded_accumulation = w.m_ConcludedAccumulation,
                        success_accumulation = w.m_SuccessAccumulation,
                        average_waiting_time = w.m_AverageWaitingTime
                    };
                }

                object routeLane = null;
                if (routeLaneLookup.TryGetComponent(stopEntity, out var lane))
                {
                    routeLane = new
                    {
                        start_lane = lane.m_StartLane.Index,
                        start_lane_version = lane.m_StartLane.Version,
                        end_lane = lane.m_EndLane.Index,
                        end_lane_version = lane.m_EndLane.Version,
                        start_curve_pos = lane.m_StartCurvePos,
                        end_curve_pos = lane.m_EndCurvePos
                    };
                }

                object stopData = null;
                if (prefabTransportStopData.HasComponent(prefabRef.m_Prefab))
                {
                    var data = prefabTransportStopData[prefabRef.m_Prefab];
                    stopData = new
                    {
                        comfort_factor = data.m_ComfortFactor,
                        loading_factor = data.m_LoadingFactor,
                        access_distance = data.m_AccessDistance,
                        boarding_time = data.m_BoardingTime,
                        transport_type = data.m_TransportType.ToString(),
                        passenger_transport = data.m_PassengerTransport,
                        cargo_transport = data.m_CargoTransport
                    };
                }

                stops.Add(new
                {
                    stop_id = stopEntity.Index,
                    stop_version = stopEntity.Version,
                    route_id = owner.m_Owner.Index,
                    route_version = owner.m_Owner.Version,
                    district_id = GetDistrictId(stopEntity),
                    position = pos,
                    access_restriction = MakeEntityRef(stop.m_AccessRestriction),
                    comfort_factor = stop.m_ComfortFactor,
                    loading_factor = stop.m_LoadingFactor,
                    flags = (uint)stop.m_Flags,
                    route_lane = routeLane,
                    waiting_passengers = waiting,
                    stop_data = stopData
                });
            }

            Publish("telex/transport", new
            {
                type = "transport",
                city_name = m_CityConfigurationSystem.cityName,
                day = absoluteDay,
                current_date = currentDate,
                lines = lines,
                stops = stops,
                line_stops = lineStops,
                line_segments = lineSegments,
                line_vehicles = lineVehicles,
                passengers_bus = m_CityStatisticsSystem.GetStatisticValue(StatisticType.PassengerCountBus),
                passengers_tram = m_CityStatisticsSystem.GetStatisticValue(StatisticType.PassengerCountTram),
                passengers_subway = m_CityStatisticsSystem.GetStatisticValue(StatisticType.PassengerCountSubway),
                passengers_train = m_CityStatisticsSystem.GetStatisticValue(StatisticType.PassengerCountTrain),
                passengers_ship = m_CityStatisticsSystem.GetStatisticValue(StatisticType.PassengerCountShip),
                passengers_ferry = m_CityStatisticsSystem.GetStatisticValue(StatisticType.PassengerCountFerry),
                passengers_airplane = m_CityStatisticsSystem.GetStatisticValue(StatisticType.PassengerCountAirplane),
                passengers_taxi = m_CityStatisticsSystem.GetStatisticValue(StatisticType.PassengerCountTaxi),
            });

            lineEntities.Dispose();
            stopEntities.Dispose();
        }
        private void WriteCitizenSnapshot(int absoluteDay, string currentDate)
        {
            var citizens = m_CitizenQuery.ToEntityArray(Allocator.Temp);
            var records = new List<object>(citizens.Length);

            var householdMemberLookup = GetComponentLookup<HouseholdMember>(true);
            var propertyRenterLookup = GetComponentLookup<PropertyRenter>(true);
            var currentDistrictLookup = GetComponentLookup<Game.Areas.CurrentDistrict>(true);

            foreach (var citizenEntity in citizens)
            {
                var citizenData = EntityManager.GetComponentData<Citizen>(citizenEntity);
                int ageGroupValue = (int)citizenData.GetAge();

                int? householdId = null;
                int? homeBuildingId = null;
                int? homeBuildingVersion = null;
                int? homeDistrictId = null;

                if (householdMemberLookup.HasComponent(citizenEntity))
                {
                    Entity household = householdMemberLookup[citizenEntity].m_Household;
                    householdId = household.Index;

                    if (propertyRenterLookup.TryGetComponent(household, out var renter))
                    {
                        Entity homeBuilding = renter.m_Property;

                        if (homeBuilding != Entity.Null && EntityManager.Exists(homeBuilding))
                        {
                            homeBuildingId = homeBuilding.Index;
                            homeBuildingVersion = homeBuilding.Version;

                            if (currentDistrictLookup.TryGetComponent(homeBuilding, out var currentDistrict)
                                && currentDistrict.m_District != Entity.Null)
                            {
                                homeDistrictId = currentDistrict.m_District.Index;
                            }
                        }
                    }
                }

                records.Add(new
                {
                    entity = citizenEntity.Index,
                    version = citizenEntity.Version,
                    household_id = householdId,
                    age = ageGroupValue,
                    education = citizenData.GetEducationLevel(),
                    state = (int)citizenData.m_State,
                    wellbeing = citizenData.m_WellBeing,
                    health = citizenData.m_Health,
                    home_building_id = homeBuildingId,
                    home_building_version = homeBuildingVersion,
                    home_district_id = homeDistrictId
                });
            }

            var snapshot = new
            {
                type = "citizens",
                city_name = m_CityConfigurationSystem.cityName,
                day = absoluteDay,
                current_date = currentDate,
                citizens = records
            };

            Publish("telex/citizens", snapshot);
            citizens.Dispose();
        }
        private void WriteBuildingSnapshot(int absoluteDay, string currentDate)
        {
            var buildings = m_BuildingQuery.ToEntityArray(Allocator.Temp);
            var records = new List<object>(buildings.Length);

            var schoolDataLookup = GetComponentLookup<Game.Prefabs.SchoolData>(true);
            var hospitalDataLookup = GetComponentLookup<Game.Prefabs.HospitalData>(true);
            var electricityLookup = GetComponentLookup<Game.Buildings.ElectricityConsumer>(true);
            var waterLookup = GetComponentLookup<Game.Buildings.WaterConsumer>(true);
            var commercialLookup = GetComponentLookup<Game.Buildings.CommercialProperty>(true);
            var industrialLookup = GetComponentLookup<Game.Buildings.IndustrialProperty>(true);
            var serviceAvailableLookup = GetComponentLookup<Game.Companies.ServiceAvailable>(true);
            var residentialLookup = GetComponentLookup<Game.Buildings.ResidentialProperty>(true);
            var officeLookup = GetComponentLookup<Game.Buildings.OfficeProperty>(true);

            Game.UI.NameSystem activeNameSystem = null;
            try
            {
                activeNameSystem = base.World.GetExistingSystemManaged<Game.UI.NameSystem>();
            }
            catch (Exception ex)
            {
                Mod.log.Error($"[Telex] Failed to retrieve NameSystem: {ex.Message}");
            }

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

                string zoneType = isResidential ? "residential"
                    : isCommercial ? "commercial"
                    : isIndustrial ? "industrial"
                    : isOffice ? "office"
                    : "other";

                bool hasBuildingData = EntityManager.HasComponent<Game.Prefabs.BuildingData>(prefab);
                Game.Prefabs.BuildingData prefabBuildingData = hasBuildingData
                    ? EntityManager.GetComponentData<Game.Prefabs.BuildingData>(prefab)
                    : default;

                // 1. RESOLVE DISTRICT NAMES
                int? districtId = null;
                string districtName = null;
                Entity targetDistrictEntity = Entity.Null;

                if (EntityManager.HasComponent<Game.Areas.CurrentDistrict>(buildingEntity))
                {
                    targetDistrictEntity = EntityManager.GetComponentData<Game.Areas.CurrentDistrict>(buildingEntity).m_District;
                }

                if (targetDistrictEntity == Entity.Null && EntityManager.HasComponent<Game.Common.Owner>(buildingEntity))
                {
                    var owner = EntityManager.GetComponentData<Game.Common.Owner>(buildingEntity).m_Owner;
                    if (EntityManager.HasComponent<Game.Areas.District>(owner))
                    {
                        targetDistrictEntity = owner;
                    }
                }

                if (targetDistrictEntity != Entity.Null)
                {
                    districtId = targetDistrictEntity.Index;

                    if (activeNameSystem != null)
                    {
                        // TryGetCustomName covers player-renamed districts,
                        // GetRenderedLabelName covers auto-generated names like "Sterling Square"
                        if (!activeNameSystem.TryGetCustomName(targetDistrictEntity, out districtName))
                        {
                            districtName = activeNameSystem.GetRenderedLabelName(targetDistrictEntity);
                        }
                    }

                    if (string.IsNullOrEmpty(districtName))
                    {
                        districtName = $"District {targetDistrictEntity.Index}";
                    }
                }
                else
                {
                    districtName = m_CityConfigurationSystem.cityName;
                }

                // 2. RESOLVE STREET NAMES via BuildingUtils.GetAddress
                // This mirrors exactly how the game itself resolves building addresses
                string streetName = "";
                int houseNumber = 0;
                Entity roadEdgeEntity = building.m_RoadEdge;

                if (roadEdgeEntity != Entity.Null)
                {
                    if (Game.Buildings.BuildingUtils.GetAddress(
                        EntityManager, buildingEntity,
                        roadEdgeEntity, building.m_CurvePosition,
                        out Entity aggregateRoad, out int number))
                    {
                        houseNumber = number;
                        if (activeNameSystem != null && aggregateRoad != Entity.Null)
                        {
                            if (!activeNameSystem.TryGetCustomName(aggregateRoad, out streetName))
                            {
                                streetName = activeNameSystem.GetRenderedLabelName(aggregateRoad);
                            }
                        }
                    }
                }

                if (streetName == null) streetName = "";

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
                    district = districtId,
                    district_name = districtName,
                    street_name = streetName,
                    house_number = houseNumber,
                    zone_type = zoneType,
                    commercial_resources = isCommercial ? commercialLookup[buildingEntity].m_Resources.ToString() : null,
                    industrial_resources = isIndustrial ? industrialLookup[buildingEntity].m_Resources.ToString() : null,
                    pos = new { x = transform.m_Position.x, y = transform.m_Position.y, z = transform.m_Position.z },
                    road_edge = roadEdgeEntity.Index,
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

