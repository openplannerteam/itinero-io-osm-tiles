using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Itinero.Attributes;
using Itinero.Graphs.Geometric.Shapes;
using Itinero.LocalGeo;
using Itinero.Logging;
using Itinero.Profiles;
using Newtonsoft.Json;
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
        /// Adds data from an individual tile.
        /// </summary>
        /// <param name="routerDb">The router db to fill.</param>
        /// <param name="globalIdMap">The global id map.</param>
        /// <param name="tile">The tile to load.</param>
        /// <param name="baseUrl">The base url of the routeable tile source.</param>
        /// <param name="vehicleCache">The vehicle cache.</param>
        internal static void AddOsmTile(this RouterDb routerDb, GlobalIdMap globalIdMap, Tile tile,
            VehicleCache vehicleCache = null, string baseUrl = BaseUrl)
        {
            var url = baseUrl + $"/{tile.Zoom}/{tile.X}/{tile.Y}";
            var stream = Download.DownloadHelper.Download(url);
            if (stream == null)
            {
                return;
            }

            Logger.Log(nameof(TileParser), Logging.TraceEventType.Information,
                $"Loading tile: {tile}");
            
            // build the vehicle cache.
            if (vehicleCache == null)
            {
                vehicleCache = new VehicleCache(routerDb.GetSupportedVehicles().ToArray());
            }
            
            var nodeLocations = new Dictionary<long, Coordinate>();
            using (var textReader = new StreamReader(stream))
            {
                var jsonObject = JObject.Parse(textReader.ReadToEnd());

                if (!(jsonObject["@graph"] is JArray graph)) return;

                foreach (var graphObject in graph)
                {
                    if (!(graphObject["@id"] is JToken idToken)) continue;
                    var id = idToken.Value<string>();

                    if (id == null) continue;

                    if (id.StartsWith("http://www.openstreetmap.org/node/"))
                    {
                        var nodeId = long.Parse(id.Substring("http://www.openstreetmap.org/node/".Length,
                            id.Length - "http://www.openstreetmap.org/node/".Length));

                        if (globalIdMap.TryGet(nodeId, out var vertexId)) continue;

                        if (!(graphObject["geo:long"] is JToken longToken)) continue;
                        var lon = longToken.Value<double>();
                        if (!(graphObject["geo:lat"] is JToken latToken)) continue;
                        var lat = latToken.Value<double>();
                        
                        nodeLocations[nodeId] = new Coordinate((float) lat, (float) lon);
                    }
                    else if (id.StartsWith("http://www.openstreetmap.org/way/"))
                    {
                        var attributes = new AttributeCollection();
                        foreach (var child in graphObject.Children())
                        {
                            if (!(child is JProperty property)) continue;

                            if (property.Name == "@id" ||
                                property.Name == "osm:nodes" ||
                                property.Name == "@type") continue;

                            if (property.Name == "rdfs:label")
                            {
                                attributes.AddOrReplace("name", property.Value.Value<string>());
                                continue;
                            }

                            var key = property.Name;
                            if (key.StartsWith("osm:"))
                            {
                                key = key.Substring(4, key.Length - 4);
                            }

                            var value = property.Value.Value<string>();
                            if (value.StartsWith("osm:"))
                            {
                                value = value.Substring(4, value.Length - 4);
                            }

                            attributes.AddOrReplace(key, value);
                        }

                        // add the edge if the attributes are of use to the vehicles defined.
                        var wayAttributes = attributes;
                        var profileWhiteList = new Whitelist();
                        if (!vehicleCache.AddToWhiteList(wayAttributes, profileWhiteList)) continue;

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
                            // way has some use, add all of it's nodes to the index.
                            continue;
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

                        if (!(graphObject["osm:nodes"] is JArray nodes)) continue;
                        
                        // add first as vertex.
                        var node = nodes[0];
                        if (!(node is JToken nodeToken)) continue;
                        var nodeIdString = nodeToken.Value<string>();
                        var nodeId = long.Parse(nodeIdString.Substring("http://www.openstreetmap.org/node/".Length,
                            nodeIdString.Length - "http://www.openstreetmap.org/node/".Length));
                        if (!globalIdMap.TryGet(nodeId, out var previousVertex))
                        {
                            if (!nodeLocations.TryGetValue(nodeId, out var nodeLocation))
                            {
                                throw new Exception($"Could not load tile {tile}: node {nodeId} missing.");
                            }
                            previousVertex = routerDb.Network.VertexCount;
                            routerDb.Network.AddVertex(previousVertex, nodeLocation.Latitude, nodeLocation.Longitude);
                            globalIdMap.Set(nodeId, previousVertex);
                        }
                        
                        // add last as vertex.
                        node = nodes[nodes.Count - 1];
                        nodeToken = (node as JToken);
                        if (nodeToken == null) continue;
                        nodeIdString = nodeToken.Value<string>();
                        nodeId = long.Parse(nodeIdString.Substring("http://www.openstreetmap.org/node/".Length,
                            nodeIdString.Length - "http://www.openstreetmap.org/node/".Length));
                        if (!globalIdMap.TryGet(nodeId, out var vertexId))
                        {
                            if (!nodeLocations.TryGetValue(nodeId, out var nodeLocation))
                            {
                                throw new Exception($"Could not load tile {tile}: node {nodeId} missing.");
                            }
                            vertexId = routerDb.Network.VertexCount;
                            routerDb.Network.AddVertex(vertexId, nodeLocation.Latitude, nodeLocation.Longitude);
                            globalIdMap.Set(nodeId, vertexId);
                        }
                        
                        var shape = new List<Coordinate>();
                        for (var n = 1; n < nodes.Count; n++)
                        {
                            node = nodes[n];
                            nodeToken = node as JToken;
                            if (node == null) continue;
                            nodeIdString = nodeToken.Value<string>();
                            nodeId = long.Parse(nodeIdString.Substring("http://www.openstreetmap.org/node/".Length,
                                nodeIdString.Length - "http://www.openstreetmap.org/node/".Length));

                            if (globalIdMap.TryGet(nodeId, out vertexId))
                            {
                                shape.Insert(0, routerDb.Network.GetVertex(previousVertex));
                                shape.Add(routerDb.Network.GetVertex(vertexId));
                                var distance = Itinero.LocalGeo.Coordinate.DistanceEstimateInMeter(shape);
                                if (distance > Itinero.Constants.DefaultMaxEdgeDistance)
                                {
                                    distance = Itinero.Constants.DefaultMaxEdgeDistance;
                                }

                                shape.RemoveAt(0);
                                shape.RemoveAt(shape.Count - 1);
                                routerDb.Network.AddEdge(previousVertex, vertexId, new Data.Network.Edges.EdgeData()
                                {
                                    MetaId = meta,
                                    Distance = distance,
                                    Profile = (ushort) profile
                                }, new ShapeEnumerable(shape));
                                shape.Clear();

                                previousVertex = vertexId;
                            }
                            else
                            {
                                if (!nodeLocations.TryGetValue(nodeId, out var nodeLocation))
                                {
                                    throw new Exception($"Could not load tile {tile}: node {nodeId} missing.");
                                }
                                shape.Add(nodeLocation);
                            }
                        }
                    }
                    else if (id.StartsWith("http://www.openstreetmap.org/relation/"))
                    {
                        Console.WriteLine(id);
                    }
                }
            }
        }
    }
}