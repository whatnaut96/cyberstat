using System;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
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
using System.Text;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Newtonsoft.Json;

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
        private EntityQuery m_CitizenQuery;
        private EntityQuery m_DistrictQuery;
        private EntityQuery m_TransportLineQuery;
        private EntityQuery m_TransportStopQuery;

        private uint m_LastHour;
        private const uint kFramesPerHour = 262144 / 24;
        private bool m_IsFirstFrame = true;

        private TcpClient m_TcpClient;
        private SslStream m_SslStream;
        private string m_ProcessorAddress;
        private int m_ProcessorPort;

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

            m_ProcessorAddress = Environment.GetEnvironmentVariable("BROADWAY_PROCESSOR_ADDRESS") ?? "localhost";
            int.TryParse(Environment.GetEnvironmentVariable("BROADWAY_PROCESSOR_PORT"), out int port);
            m_ProcessorPort = port > 0 ? port : 2146;

            m_LastHour = uint.MaxValue;

            ConnectTcp();
        }

        protected override void OnDestroy()
        {
            DisconnectTcp();
            base.OnDestroy();
        }

        private void ConnectTcp()
        {
            try
            {
                DisconnectTcp();
                m_TcpClient = new TcpClient(m_ProcessorAddress, m_ProcessorPort);
                m_SslStream = new SslStream(
                    m_TcpClient.GetStream(),
                    false,
                    (sender, cert, chain, errors) => true // accept self-signed for now
                );
                m_SslStream.AuthenticateAsClient(m_ProcessorAddress);
                Mod.log.Info($"Telex: TCP/TLS connected to {m_ProcessorAddress}:{m_ProcessorPort}");
            }
            catch (Exception ex)
            {
                Mod.log.Error($"Telex: TCP/TLS connection failed: {ex.Message}");
                DisconnectTcp();
            }
        }

        private void DisconnectTcp()
        {
            try { m_SslStream?.Dispose(); } catch { }
            try { m_TcpClient?.Dispose(); } catch { }
            m_SslStream = null;
            m_TcpClient = null;
        }

        private void Publish(object payload)
        {
            if (m_SslStream == null || !m_TcpClient.Connected)
            {
                Mod.log.Warn("Telex: TCP not connected, attempting reconnect...");
                ConnectTcp();
                if (m_SslStream == null)
                {
                    Mod.log.Error("Telex: Reconnect failed, dropping payload");
                    return;
                }
            }

            try
            {
                var json = JsonConvert.SerializeObject(payload);
                var bytes = Encoding.UTF8.GetBytes(json + "\n");
                m_SslStream.Write(bytes, 0, bytes.Length);
                m_SslStream.Flush();
                Mod.log.Info($"Telex: Published {bytes.Length} bytes");
            }
            catch (Exception ex)
            {
                Mod.log.Error($"Telex: Publish failed: {ex.Message}");
                DisconnectTcp();
            }
        }

        protected override void OnUpdate()
        {
            var timeData = m_TimeDataQuery.GetSingleton<Game.Common.TimeData>();
            uint currentHour = (m_SimulationSystem.frameIndex - timeData.m_FirstFrame) / kFramesPerHour;

            if (currentHour == m_LastHour)
                return;

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

            m_TaxSystem.Update();
            var taxRates = m_TaxSystem.GetTaxRates();
            var residentialTaxes = GenerateResidentialTaxSnapshot(taxRates);
            var resourceSnapshots = GenerateResourceSnapshot(taxRates);

            var cargoSnapshot = new Dictionary<string, int>
            {
                { "train", m_CityStatisticsSystem.GetStatisticValue(StatisticType.CargoCountTrain) },
                { "ship", m_CityStatisticsSystem.GetStatisticValue(StatisticType.CargoCountShip) },
                { "airplane", m_CityStatisticsSystem.GetStatisticValue(StatisticType.CargoCountAirplane) },
                { "truck", m_CityStatisticsSystem.GetStatisticValue(StatisticType.CargoCountTruck) }
            };

            var crime = new
            {
                crime_count = m_CityStatisticsSystem.GetStatisticValue(StatisticType.CrimeCount),
                crime_rate = m_CityStatisticsSystem.GetStatisticValue(StatisticType.CrimeRate),
                escaped_arrests = m_CityStatisticsSystem.GetStatisticValue(StatisticType.EscapedArrestCount),
            };

            var economics = new
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
                residential_taxable_income = m_CityStatisticsSystem.GetStatisticValue(StatisticType.ResidentialTaxableIncome),
                commercial_taxable_income = m_CityStatisticsSystem.GetStatisticValue(StatisticType.CommercialTaxableIncome),
                industrial_taxable_income = m_CityStatisticsSystem.GetStatisticValue(StatisticType.IndustrialTaxableIncome),
                office_taxable_income = m_CityStatisticsSystem.GetStatisticValue(StatisticType.OfficeTaxableIncome),
                tax_rates_residential = residentialTaxes,
            };

            var tourismSnapshot = new
            {
                tourists = m_CityStatisticsSystem.GetStatisticValue(StatisticType.TouristCount),
                tourist_income = m_CityStatisticsSystem.GetStatisticValue(StatisticType.TouristIncome),
                lodging_total = m_CityStatisticsSystem.GetStatisticValue(StatisticType.LodgingTotal),
                lodging_used = m_CityStatisticsSystem.GetStatisticValue(StatisticType.LodgingUsed),
            };

            var laborSnapshot = new
            {
                // Labor
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

            var mailSnapshot = new
            {
                collected_mail = m_CityStatisticsSystem.GetStatisticValue(StatisticType.CollectedMail),
                delivered_mail = m_CityStatisticsSystem.GetStatisticValue(StatisticType.DeliveredMail),
            };

            var payload = new
            {
                city_name = m_CityConfigurationSystem.cityName,
                date = dateString,
                absolute_day = absoluteDay,

                // TODO: Determine if this is even worth keeping
                homeless = m_CityStatisticsSystem.GetStatisticValue(StatisticType.HomelessCount),
                birth_rate = m_CityStatisticsSystem.GetStatisticValue(StatisticType.BirthRate),
                death_rate = m_CityStatisticsSystem.GetStatisticValue(StatisticType.DeathRate),

                economy = economics,
                resources = resourceSnapshots,
                cargo = cargoSnapshot,
                tourism = tourismSnapshot,
                labor = laborSnapshot,
                mail = mailSnapshot,
                districts = GenerateDistrictSnapshot(),
                roads = GenerateRoadSnapshot(),
                buildings = GenerateBuildingSnapshot(),
                transport = GenerateTransportSnapshot(),
                citizens = GenerateCitizenSnapshot()
            };

            Publish(payload);
        }

        private Dictionary<string, int> GenerateResidentialTaxSnapshot(NativeArray<int> taxRates)
        {
            return new Dictionary<string, int>
            {
                { "uneducated", taxRates[(int)TaxAreaType.Residential] + taxRates[5] },
                { "poorly_educated", taxRates[(int)TaxAreaType.Residential] + taxRates[6] },
                { "educated", taxRates[(int)TaxAreaType.Residential] + taxRates[7] },
                { "well_educated", taxRates[(int)TaxAreaType.Residential] + taxRates[8] },
                { "highly_educated", taxRates[(int)TaxAreaType.Residential] + taxRates[9] }
            };
        }

        private class ResourceSnapshotData
        {
            public int trade_value { get; set; }
            public int commercial_tax { get; set; }
            public int industrial_tax { get; set; }
            public int office_tax { get; set; }
        }

        private Dictionary<string, ResourceSnapshotData> GenerateResourceSnapshot(NativeArray<int> taxRates)
        {
            var result = new Dictionary<string, ResourceSnapshotData>();

            foreach (var resource in kTradeResources)
            {
                int idx = EconomyUtils.GetResourceIndex(resource);
                if (idx == -1) continue;

                string resourceName = resource.ToString().ToLower();
                result[resourceName] = new ResourceSnapshotData
                {
                    trade_value = m_CityStatisticsSystem.GetStatisticValue(StatisticType.Trade, idx),
                    commercial_tax = taxRates[(int)TaxAreaType.Commercial] + taxRates[10 + idx],
                    industrial_tax = taxRates[(int)TaxAreaType.Industrial] + taxRates[10 + idx],
                    office_tax = taxRates[(int)TaxAreaType.Office] + taxRates[10 + idx]
                };
            }

            return result;
        }

        private List<object> GenerateRoadSnapshot()
        {
            var ownerLookup = GetComponentLookup<Game.Common.Owner>(true);

            var lanes = m_TrafficQuery.ToEntityArray(Allocator.Temp);
            var lanesByEdge = new Dictionary<int, List<object>>();

            foreach (var laneEntity in lanes)
            {
                var flow = EntityManager.GetComponentData<Game.Net.LaneFlow>(laneEntity);
                var edgeLane = EntityManager.GetComponentData<Game.Net.EdgeLane>(laneEntity);
                var carLane = EntityManager.GetComponentData<Game.Net.CarLane>(laneEntity);

                float4 dur = flow.m_Duration;
                float4 dist = flow.m_Distance;
                float4 speed = math.select(0f, dist / dur, dur > 0f);

                int ownerEdgeIndex = -1;
                if (ownerLookup.TryGetComponent(laneEntity, out var owner))
                    ownerEdgeIndex = owner.m_Owner.Index;

                if (!lanesByEdge.TryGetValue(ownerEdgeIndex, out var list))
                    lanesByEdge[ownerEdgeIndex] = list = new List<object>();

                list.Add(new
                {
                    entity = laneEntity.Index,
                    owner_edge = ownerEdgeIndex,
                    edge_delta_start = edgeLane.m_EdgeDelta.x,
                    edge_delta_end = edgeLane.m_EdgeDelta.y,
                    carriageway_group = carLane.m_CarriagewayGroup,
                    speed = new float[] { speed.x, speed.y, speed.z, speed.w },
                    duration = new float[] { dur.x, dur.y, dur.z, dur.w },
                    distance = new float[] { dist.x, dist.y, dist.z, dist.w },
                    next_x = flow.m_Next.x,
                    next_y = flow.m_Next.y
                });
            }

            lanes.Dispose();

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

                object roadTraffic = null;
                if (EntityManager.HasComponent<Game.Net.Road>(edgeEntity))
                {
                    var road = EntityManager.GetComponentData<Game.Net.Road>(edgeEntity);
                    var dur0 = road.m_TrafficFlowDuration0;
                    var dur1 = road.m_TrafficFlowDuration1;
                    var dist0 = road.m_TrafficFlowDistance0;
                    var dist1 = road.m_TrafficFlowDistance1;
                    float4 speed0 = math.select(0f, dist0 / dur0, dur0 > 0f);
                    float4 speed1 = math.select(0f, dist1 / dur1, dur1 > 0f);
                    float4 vol0 = math.sqrt((dist0 + dist1) * 2.6666667f);
                    roadTraffic = new
                    {
                        speed0 = new float[] { speed0.x, speed0.y, speed0.z, speed0.w },
                        speed1 = new float[] { speed1.x, speed1.y, speed1.z, speed1.w },
                        volume = new float[] { vol0.x, vol0.y, vol0.z, vol0.w },
                        duration0 = new float[] { dur0.x, dur0.y, dur0.z, dur0.w },
                        duration1 = new float[] { dur1.x, dur1.y, dur1.z, dur1.w },
                        distance0 = new float[] { dist0.x, dist0.y, dist0.z, dist0.w },
                        distance1 = new float[] { dist1.x, dist1.y, dist1.z, dist1.w }
                    };
                }

                lanesByEdge.TryGetValue(edgeEntity.Index, out var edgeLanes);

                records.Add(new
                {
                    entity_id = edgeEntity.Index,
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
                    road_traffic = roadTraffic,
                    lanes = edgeLanes
                });
            }

            edges.Dispose();
            return records;
        }

        private object MakeEntityRef(Entity entity)
        {
            return entity == Entity.Null ? null : (object)new { entity = entity.Index, version = entity.Version };
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

        private List<object> GenerateDistrictSnapshot()
        {
            var districts = m_DistrictQuery.ToEntityArray(Allocator.Temp);
            var records = new List<object>(districts.Length);

            Game.UI.NameSystem activeNameSystem = null;
            try { activeNameSystem = base.World.GetExistingSystemManaged<Game.UI.NameSystem>(); } catch { }

            foreach (var districtEntity in districts)
            {
                var district = EntityManager.GetComponentData<Game.Areas.District>(districtEntity);
                string districtName = null;
                if (activeNameSystem != null)
                {
                    if (!activeNameSystem.TryGetCustomName(districtEntity, out districtName))
                        districtName = activeNameSystem.GetRenderedLabelName(districtEntity);
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

            districts.Dispose();
            return records;
        }

        private object GenerateTransportSnapshot()
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

                int? routeNumber = routeNumberLookup.HasComponent(lineEntity)
                    ? routeNumberLookup[lineEntity].m_Number
                    : (int?)null;

                object routeInfo = null;
                if (routeInfoLookup.HasComponent(lineEntity))
                {
                    var info = routeInfoLookup[lineEntity];
                    routeInfo = new { duration = info.m_Duration, distance = info.m_Distance, flags = (int)info.m_Flags };
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
                            connectedEntity = connected.m_Connected;

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

            lineEntities.Dispose();
            stopEntities.Dispose();

            return new
            {
                lines,
                stops,
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
                passengers_taxi = m_CityStatisticsSystem.GetStatisticValue(StatisticType.PassengerCountTaxi)
            };
        }

        private List<object> GenerateCitizenSnapshot()
        {
            var citizens = m_CitizenQuery.ToEntityArray(Allocator.Temp);
            var records = new List<object>(citizens.Length);

            var householdMemberLookup = GetComponentLookup<HouseholdMember>(true);
            var propertyRenterLookup = GetComponentLookup<PropertyRenter>(true);
            var currentDistrictLookup = GetComponentLookup<Game.Areas.CurrentDistrict>(true);

            foreach (var citizenEntity in citizens)
            {
                var citizenData = EntityManager.GetComponentData<Citizen>(citizenEntity);

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
                                homeDistrictId = currentDistrict.m_District.Index;
                        }
                    }
                }

                records.Add(new
                {
                    entity = citizenEntity.Index,
                    household_id = householdId,
                    age = (int)citizenData.GetAge(),
                    education = citizenData.GetEducationLevel(),
                    state = (int)citizenData.m_State,
                    wellbeing = citizenData.m_WellBeing,
                    health = citizenData.m_Health,
                    home_building_id = homeBuildingId,
                });
            }

            citizens.Dispose();
            return records;
        }

        private List<object> GenerateBuildingSnapshot()
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
            try { activeNameSystem = base.World.GetExistingSystemManaged<Game.UI.NameSystem>(); }
            catch (Exception ex) { Mod.log.Error($"[Telex] Failed to retrieve NameSystem: {ex.Message}"); }

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

                int? districtId = null;
                string districtName = null;
                Entity targetDistrictEntity = Entity.Null;

                if (EntityManager.HasComponent<Game.Areas.CurrentDistrict>(buildingEntity))
                    targetDistrictEntity = EntityManager.GetComponentData<Game.Areas.CurrentDistrict>(buildingEntity).m_District;

                if (targetDistrictEntity == Entity.Null && EntityManager.HasComponent<Game.Common.Owner>(buildingEntity))
                {
                    var owner = EntityManager.GetComponentData<Game.Common.Owner>(buildingEntity).m_Owner;
                    if (EntityManager.HasComponent<Game.Areas.District>(owner))
                        targetDistrictEntity = owner;
                }

                if (targetDistrictEntity != Entity.Null)
                {
                    districtId = targetDistrictEntity.Index;
                    if (activeNameSystem != null)
                    {
                        if (!activeNameSystem.TryGetCustomName(targetDistrictEntity, out districtName))
                            districtName = activeNameSystem.GetRenderedLabelName(targetDistrictEntity);
                    }
                    if (string.IsNullOrEmpty(districtName))
                        districtName = $"District {targetDistrictEntity.Index}";
                }
                else
                {
                    districtName = m_CityConfigurationSystem.cityName;
                }

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
                                streetName = activeNameSystem.GetRenderedLabelName(aggregateRoad);
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

            buildings.Dispose();
            return records;
        }
    }
}
