using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Itinero.Attributes;
using Itinero.Graphs.Geometric.Shapes;
using Itinero.IO.Osm.Tiles.Parsers.Semantics;
using Itinero.LocalGeo;
using Itinero.Logging;
using Itinero.Profiles;
using Newtonsoft.Json.Linq;

namespace Itinero.IO.Osm.Tiles.Parsers
{
    public static class TileParser
    {
        /// <summary>
        /// The base url to fetch the tiles from.
        /// </summary>
        public const string BaseUrl = "https://tiles.openplanner.team/planet";

        /// <summary>
        /// The function to download from a given url.
        /// </summary>
        public static Func<string, Stream> DownloadFunc = Download.DownloadHelper.Download;
        
        private static readonly Lazy<Dictionary<string, TagMapperConfig>> ReverseMappingLazy = new Lazy<Dictionary<string, TagMapperConfig>>(
            () => TagMapperConfigParser.Parse(LoadEmbeddedResourceStream("Itinero.IO.Osm.Tiles.ontology.mapping_config.json")));

        /// <summary>
        /// Adds data from an individual tile.
        /// </summary>
        /// <param name="routerDb">The router db to fill.</param>
        /// <param name="globalIdMap">The global id map.</param>
        /// <param name="tile">The tile to load.</param>
        /// <param name="vehicleCache">The vehicle cache.</param>
        /// <param name="baseUrl">The base url of the routable tile source.</param>
        internal static bool AddOsmTile(this RouterDb routerDb, GlobalIdMap globalIdMap, Tile tile,
            VehicleCache vehicleCache = null, string baseUrl = BaseUrl)
        {
            var updated = false;
            try
            {
                var url = baseUrl + $"/{tile.Zoom}/{tile.X}/{tile.Y}";
                var stream = DownloadFunc(url);
                if (stream == null)
                {
                    return false;
                }

                var nodeLocations = new Dictionary<long, (Coordinate location, bool inTile)>();
                var waysData = new Dictionary<long, (List<long> nodes, AttributeCollection attributes)>();
                var nodes = new HashSet<long>();
                var coreNodes = new HashSet<long>();
                using (var textReader = new StreamReader(stream))
                {
                    var json = textReader.ReadToEnd();
                    var jsonObject = JObject.Parse(json);

                    if (!(jsonObject["@graph"] is JArray graph)) return false;

                    foreach (var graphObject in graph)
                    {
                        if (!(graphObject["@id"] is JToken idToken)) continue;
                        var id = idToken.Value<string>();

                        if (id == null) continue;

                        if (id.StartsWith("http://www.openstreetmap.org/node/"))
                        {
                            // parse as a node.
                            var nodeId = long.Parse(id.Substring("http://www.openstreetmap.org/node/".Length,
                                id.Length - "http://www.openstreetmap.org/node/".Length));

                            if (!(graphObject["geo:long"] is JToken longToken)) continue;
                            var lon = longToken.Value<double>();
                            if (!(graphObject["geo:lat"] is JToken latToken)) continue;
                            var lat = latToken.Value<double>();

                            // determine if node is in tile or not.
                            var inTile = Tile.WorldToTile(lon, lat,
                                             tile.Zoom).LocalId == tile.LocalId;
                            nodeLocations[nodeId] = (new Coordinate((float) lat, (float) lon),
                                inTile);
                        }
                        else if (id.StartsWith("http://www.openstreetmap.org/way/"))
                        {
                            // parse as a way.
                            var wayId = long.Parse(id.Substring("http://www.openstreetmap.org/way/".Length,
                                id.Length - "http://www.openstreetmap.org/way/".Length));

                            // interpret all tags with defined semantics.
                            var attributes = GetTags(graphObject, ReverseMappingLazy.Value);
                            attributes.AddOrReplace("way_id", wayId.ToInvariantString());
                            attributes.AddOrReplace("tile_x", tile.X.ToInvariantString());
                            attributes.AddOrReplace("tile_y", tile.Y.ToInvariantString());

                            // include all raw tags (if any).
                            if ((graphObject["osm:hasTag"] is JArray rawTags))
                            {
                                for (var n = 0; n < rawTags.Count; n++)
                                {
                                    var rawTag = rawTags[n];
                                    if (!(rawTag is JValue rawTagValue)) continue;

                                    var keyValue = rawTagValue.Value<string>();
                                    var keyValueSplit = keyValue.Split('=');
                                    if (keyValueSplit.Length != 2) continue;

                                    attributes.AddOrReplace(keyValueSplit[0], keyValueSplit[1]);
                                }
                            }

                            // parse nodes.
                            if (!(graphObject["osm:hasNodes"] is JArray wayNodes)) continue;

                            var nodeIds = new List<long>();
                            for (var n = 0; n < wayNodes.Count; n++)
                            {
                                var nodeToken = wayNodes[n];
                                var nodeIdString = nodeToken.Value<string>();
                                var nodeId = long.Parse(nodeIdString.Substring(
                                    "http://www.openstreetmap.org/node/".Length,
                                    nodeIdString.Length - "http://www.openstreetmap.org/node/".Length));
                                nodeIds.Add(nodeId);

                                if (n == 0 || n == wayNodes.Count - 1)
                                {
                                    // first and last nodes always core.
                                    coreNodes.Add(nodeId);
                                }
                                else if (nodes.Contains(nodeId))
                                {
                                    // second time this node was hit.
                                    coreNodes.Add(nodeId);
                                }

                                nodes.Add(nodeId);
                            }

                            waysData[wayId] = (nodeIds, attributes);
                        }
                        else if (id.StartsWith("http://www.openstreetmap.org/relation/"))
                        {
                            // parse as a relation.
                            // TODO: parse as a relation.
                        }
                    }

                    var shape = new List<Coordinate>();
                    foreach (var wayPairs in waysData)
                    {
                        // prepare for next way.
                        shape.Clear();
                        var previousVertex = Itinero.Constants.NO_VERTEX;

                        // get way data.
                        var wayNodes = wayPairs.Value.nodes;
                        var attributes = wayPairs.Value.attributes;

                        // verify way data and spit out a warning if a way has <= 1 node.
                        if (wayNodes.Count <= 1)
                        {
                            Itinero.Logging.Logger.Log($"{nameof(TileParser)}.{nameof(AddOsmTile)}",
                                TraceEventType.Warning,
                                $"A way was detected with <= 1 nodes.");
                            continue;
                        }

                        // iterate over the way segments and add them as edges or part of the next edge.
                        for (var n = 0; n < wayNodes.Count - 1; n++)
                        {
                            var node1Id = wayNodes[n];
                            var node2Id = wayNodes[n + 1];

                            // get the nodes data.
                            if (!nodeLocations.TryGetValue(node1Id, out var node1Data))
                            {
                                Itinero.Logging.Logger.Log(nameof(TileParser), TraceEventType.Warning,
                                    $"Could not load way {wayPairs.Key} in {tile}: node {node1Id} missing.");
                                break;
                            }

                            if (!nodeLocations.TryGetValue(node2Id, out var node2Data))
                            {
                                Itinero.Logging.Logger.Log(nameof(TileParser), TraceEventType.Warning,
                                    $"Could not load way {wayPairs.Key} in {tile}: node {node2Id} missing.");
                                break;
                            }

                            // add attributes and move on if failed.
                            var (profile, meta) = AddProfileAndMeta(routerDb, vehicleCache, attributes);
                            if (profile == ushort.MaxValue) continue;

                            // always add segments that cross tile boundaries.
                            // TODO: we can probably do better and add only one of the nodes as core but for now to keep complexity down we add both.
                            if (!node1Data.inTile || !node2Data.inTile)
                            {
                                coreNodes.Add(node1Id);
                                coreNodes.Add(node2Id);
                            }

                            // if node1 is core make sure to add it.
                            if (coreNodes.Contains(node1Id))
                            {
                                // add node1 as vertex but check if it already exists.
                                if (!globalIdMap.TryGet(node1Id, out var vertex))
                                {
                                    vertex = routerDb.Network.VertexCount;
                                    routerDb.Network.AddVertex(vertex, node1Data.location.Latitude,
                                        node1Data.location.Longitude);
                                    globalIdMap.Set(node1Id, vertex);
                                    updated = true;
                                }

                                // check if this segment wasn't just opened the iteration before.
                                if (vertex != previousVertex)
                                {
                                    // close previous segment if any.
                                    if (previousVertex != Itinero.Constants.NO_VERTEX)
                                    {
                                        routerDb.Network.AddEdge(previousVertex, vertex,
                                            new Data.Network.Edges.EdgeData()
                                            {
                                                MetaId = meta,
                                                Distance = Distance(routerDb, previousVertex, vertex, shape),
                                                Profile = profile
                                            }, new ShapeEnumerable(shape));
                                        updated = true;
                                        shape.Clear();
                                    }

                                    // start a new segment if the end of this one is in tile.
                                    previousVertex = Itinero.Constants.NO_VERTEX;
                                    if (node1Data.inTile)
                                    {
                                        previousVertex = vertex;
                                    }
                                }
                            }

                            // if the second node is also core, close the segment.
                            if (coreNodes.Contains(node2Id))
                            {
                                // add node2 as vertex but check if it already exists.
                                if (!globalIdMap.TryGet(node2Id, out var vertex))
                                {
                                    vertex = routerDb.Network.VertexCount;
                                    routerDb.Network.AddVertex(vertex, node2Data.location.Latitude,
                                        node2Data.location.Longitude);
                                    globalIdMap.Set(node2Id, vertex);
                                    updated = true;
                                }

                                // if this segment overlaps, always add it.
                                if (!node1Data.inTile || !node2Data.inTile)
                                {
                                    if (!globalIdMap.TryGet(node1Id, out previousVertex))
                                        throw new Exception(
                                            "Cannot add segment overlapping tile boundary, node should have already been added.");
                                    routerDb.Network.AddEdge(previousVertex, vertex, new Data.Network.Edges.EdgeData()
                                    {
                                        MetaId = meta,
                                        Distance = Distance(routerDb, previousVertex, vertex, shape),
                                        Profile = profile
                                    }, new ShapeEnumerable(shape));
                                    updated = true;
                                    shape.Clear();
                                }
                                else
                                {
                                    // close previous segment if any.
                                    if (previousVertex != Itinero.Constants.NO_VERTEX)
                                    {
                                        routerDb.Network.AddEdge(previousVertex, vertex,
                                            new Data.Network.Edges.EdgeData()
                                            {
                                                MetaId = meta,
                                                Distance = Distance(routerDb, previousVertex, vertex, shape),
                                                Profile = profile
                                            }, new ShapeEnumerable(shape));
                                        updated = true;
                                        shape.Clear();
                                    }
                                }

                                // start a new segment if the end of this one is in tile.
                                previousVertex = Itinero.Constants.NO_VERTEX;
                                if (node2Data.inTile)
                                {
                                    previousVertex = vertex;
                                }
                            }
                            else
                            {
                                // add as shape point if there is an active segment.
                                if (previousVertex != Itinero.Constants.NO_VERTEX)
                                {
                                    shape.Add(node2Data.location);
                                }
                            }
                        }
                    }

                    return updated;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                return updated;
            }
        }

        private static (ushort profile, uint meta) AddProfileAndMeta(RouterDb routerDb, VehicleCache vehicleCache, AttributeCollection attributes)
        {
            // add the edge if the attributes are of use to the vehicles defined.
            var wayAttributes = attributes;
            var profileWhiteList = new Whitelist();
            if (!vehicleCache.AddToWhiteList(wayAttributes, profileWhiteList)) return (ushort.MaxValue, uint.MaxValue);

            // way has some use.
            // build profile and meta-data.
            var profileTags = new AttributeCollection();
            var metaTags = new AttributeCollection();
            foreach (var tag in wayAttributes)
            {
                if (profileWhiteList.Contains(tag.Key))
                {
                    profileTags.AddOrReplace(tag);
                }
                else if (vehicleCache.Vehicles.IsOnProfileWhiteList(tag.Key))
                {
                    metaTags.AddOrReplace(tag);
                }
                else if (vehicleCache.Vehicles.IsOnMetaWhiteList(tag.Key))
                {
                    metaTags.AddOrReplace(tag);
                }
            }

            if (!vehicleCache.AnyCanTraverse(profileTags))
            {
                // way has no use of any profile.
                return (ushort.MaxValue, uint.MaxValue);
            }

            // get profile and meta-data id's.
            var profileCount = routerDb.EdgeProfiles.Count;
            var profile = routerDb.EdgeProfiles.Add(profileTags);
            if (profileCount != routerDb.EdgeProfiles.Count)
            {
                var stringBuilder = new StringBuilder();
                foreach (var att in profileTags)
                {
                    stringBuilder.Append(att.Key);
                    stringBuilder.Append('=');
                    stringBuilder.Append(att.Value);
                    stringBuilder.Append(' ');
                }

                Logger.Log(nameof(TileParser), Logging.TraceEventType.Information,
                    "Normalized: # profiles {0}: {1}", routerDb.EdgeProfiles.Count,
                    stringBuilder.ToInvariantString());
            }

            if (profile > Data.Edges.EdgeDataSerializer.MAX_PROFILE_COUNT)
            {
                throw new Exception(
                    "Maximum supported profiles exceeded, make sure only routing tags are included in the profiles.");
            }

            var meta = routerDb.EdgeMeta.Add(metaTags);

            return ((ushort)profile, meta);
        }

        private static float Distance(RouterDb routerDb, uint vertex1, uint vertex2, IEnumerable<Coordinate> shape = null)
        {
            var distance = 0f;
            var previous = routerDb.Network.GetVertex(vertex1);

            if (shape != null)
            {
                foreach (var s in shape)
                {
                    distance += Coordinate.DistanceEstimateInMeter(previous, s);
                    previous = s;
                }
            }

            var last = routerDb.Network.GetVertex(vertex2);
            distance += Coordinate.DistanceEstimateInMeter(previous, last);

            return distance;
        }

        /// <summary>
        /// Gets the OSM tags from the given node/way or relation.
        /// </summary>
        /// <param name="osmGeo">The node, way or relation json-ld part.</param>
        /// <param name="reverseMappings">The reverse mappings.</param>
        /// <returns>The tags.</returns>
        private static AttributeCollection GetTags(JToken osmGeo, Dictionary<string, TagMapperConfig> reverseMappings)
        {
            var attributes = new AttributeCollection();
                        
            // interpret all tags with defined semantics.
            foreach (var child in osmGeo.Children())
            {
                if (!(child is JProperty property)) continue;

                if (property.Name == "@id" ||
                    property.Name == "@type") continue;
                if (property.Value is JArray) continue;

                var attribute = property.Map(reverseMappings);
                if (attribute == null) continue;

                attributes.AddOrReplace(attribute.Value.Key, attribute.Value.Value);
            }

            return attributes;
        }
        
        /// <summary>
        /// Loads a string from an embedded resource stream.
        /// </summary>
        internal static Stream LoadEmbeddedResourceStream(string name)
        {
            return typeof(TileParser).Assembly.GetManifestResourceStream(name);
        }
    }
}