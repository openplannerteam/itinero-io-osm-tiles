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
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Newtonsoft.Json;

namespace Itinero.IO.Osm.Tiles.Parsers
{
    internal static class GeoJsonTileParser
    {
        // TODO: linked-data magic to automagically discover the correct URL and zoom-level.

        /// <summary>
        /// The base url to fetch the tiles from.
        /// </summary>
        private static string BaseUrl = "http://tiles.itinero.tech";

        private static string Vertex1AttributeName = "vertex1";
        private static string Vertex2AttributeName = "vertex2";

        private static GeoJsonSerializer GeoJsonSerializer = (GeoJsonSerializer)GeoJsonSerializer.Create();
        
        /// <summary>
        /// Adds data from an individual tile.
        /// </summary>
        /// <param name="routerDb">The routerdb to fill.</param>
        /// <param name="globalIdMap">The global id map.</param>
        /// <param name="tile">The tile to load.</param>
        internal static void AddOsmTile(this RouterDb routerDb, GlobalIdMap globalIdMap, Tile tile, VehicleCache vehicleCache = null)
        {
            var url = BaseUrl + $"/{tile.Zoom}/{tile.X}/{tile.Y}.geojson";
            var stream = Download.DownloadHelper.Download(url);
            if (stream == null)
            {
                return;
            }

            var features =
                GeoJsonSerializer.Deserialize<FeatureCollection>(new JsonTextReader(new StreamReader(stream)));
            
            var localIdMap = new Dictionary<long, uint>();
            var network = routerDb.Network;
            
            // build the vehicle cache.
            if (vehicleCache == null)
            {
                vehicleCache = new VehicleCache(routerDb.GetSupportedVehicles().ToArray());
            }
            
            foreach (var feature in features.Features)
            {
                if (!(feature.Geometry is LineString lineString)) continue;
                
                // parse attributes.
                var attributes = new AttributeCollection();
                var names = feature.Attributes.GetNames();
                var values = feature.Attributes.GetValues();
                var global1 = Constants.GLOBAL_ID_EMPTY;
                var global2 = Constants.GLOBAL_ID_EMPTY;
                for (var i = 0; i < names.Length; i++)
                {
                    var name = names[i];
                    if (name == Vertex1AttributeName)
                    {
                        if (values[i].TryParseGlobalId(out global1))
                        {
                            global1 = Constants.GLOBAL_ID_EMPTY;
                        }

                        continue;
                    }
                    if (name == Vertex2AttributeName)
                    {
                        if (values[i].TryParseGlobalId(out global2))
                        {
                            global2 = Constants.GLOBAL_ID_EMPTY;
                        }

                        continue;
                    }
                    
                    // any other data considered valid attributes for now.
                    if (values[i].TryParseAttributeValue(out var value))
                    {
                        attributes.AddOrReplace(name, value);
                    }
                }
                if (global1 == Constants.GLOBAL_ID_EMPTY ||
                    global2 == Constants.GLOBAL_ID_EMPTY)
                {
                    // this is pretty severe, don't fail but log.
                    Logger.Log(nameof(GeoJsonSerializer), TraceEventType.Error,
                        $"Tile {tile} contains a linestring feature without valid vertex ids.");
                    continue;
                }
                
                // get the vertex locations and check if they are inside the tile or not.
                var vertex1Location = new Coordinate((float)lineString.Coordinates[0].Y, (float)lineString.Coordinates[0].X);
                var vertex2Location = new Coordinate((float)lineString.Coordinates[lineString.Coordinates.Length - 1].Y, 
                    (float)lineString.Coordinates[lineString.Coordinates.Length - 1].X);
                var vertex1Outside = !tile.IsInside(vertex1Location);
                var vertex2Outside = !tile.IsInside(vertex2Location);

                // figure out if vertices are already mapped, if yes, get their ids, if not add them.
                var vertex1Global = false;
                if ((vertex1Outside || vertex2Outside) &&
                    globalIdMap.TryGet(global1, out var vertex1))
                {
                    vertex1Global = true;
                }
                else
                {
                    if (!localIdMap.TryGetValue(global1, out vertex1))
                    {
                        vertex1 = Itinero.Constants.NO_VERTEX;
                    
                        if (vertex1 == Itinero.Constants.NO_VERTEX)
                        { // no vertex yet, create one.
                            vertex1 = network.VertexCount;
                            network.AddVertex(vertex1, vertex1Location.Latitude, vertex1Location.Longitude);
                        }
                    }
                }
                var vertex2Global = false;
                if ((vertex1Outside || vertex2Outside) &&
                    globalIdMap.TryGet(global2, out var vertex2))
                {
                    vertex2Global = true;
                }
                else
                {
                    if (!localIdMap.TryGetValue(global1, out vertex2))
                    {
                        vertex2 = Itinero.Constants.NO_VERTEX;
                    
                        if (vertex2 == Itinero.Constants.NO_VERTEX)
                        { // no vertex yet, create one.
                            vertex2 = network.VertexCount;
                            network.AddVertex(vertex2, vertex2Location.Latitude, vertex2Location.Longitude);
                        }
                    }
                }
                
                // add the edge if needed.
                if (vertex1Global || vertex2Global)
                { // edge was already added in another tile.
                    continue;
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
                    return;
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

                    Logger.Log(nameof(GeoJsonTileParser), Logging.TraceEventType.Information,
                        "Normalized: # profiles {0}: {1}", routerDb.EdgeProfiles.Count,
                        stringBuilder.ToInvariantString());
                }
                if (profile > Data.Edges.EdgeDataSerializer.MAX_PROFILE_COUNT)
                {
                    throw new Exception(
                        "Maximum supported profiles exceeded, make sure only routing tags are included in the profiles.");
                }
                var meta = routerDb.EdgeMeta.Add(metaTags);
                
                // calculate distance and build shape.
                var shape = new List<Coordinate>();
                var distance = 0f;
                for (var i = 0; i < lineString.Coordinates.Length; i++)
                {
                    var shapePoint = new Coordinate((float)lineString.Coordinates[i].Y, (float)lineString.Coordinates[i].X);
                    if (i > 0)
                    {
                        distance += Coordinate.DistanceEstimateInMeter(shape[shape.Count - 1], shapePoint);
                    }
                    shape.Add(shapePoint);
                }
                shape.RemoveAt(0);
                shape.RemoveAt(shape.Count - 1);
                
                network.AddEdge(vertex1, vertex2, new Data.Network.Edges.EdgeData()
                {
                    MetaId = meta,
                    Distance = distance,
                    Profile = (ushort)profile
                }, new ShapeEnumerable(shape));
            }
        }

        internal static bool TryParseGlobalId(this object value, out long globalId)
        {
            globalId = 0;
            return false;
        }

        internal static bool TryParseAttributeValue(this object value, out string attribute)
        {
            attribute = string.Empty;
            return false;
        }
    }
}