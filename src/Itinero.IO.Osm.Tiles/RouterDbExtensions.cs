/*
 *  Licensed to SharpSoftware under one or more contributor
 *  license agreements. See the NOTICE file distributed with this work for 
 *  additional information regarding copyright ownership.
 * 
 *  SharpSoftware licenses this file to you under the Apache License, 
 *  Version 2.0 (the "License"); you may not use this file except in 
 *  compliance with the License. You may obtain a copy of the License at
 * 
 *       http://www.apache.org/licenses/LICENSE-2.0
 * 
 *  Unless required by applicable law or agreed to in writing, software
 *  distributed under the License is distributed on an "AS IS" BASIS,
 *  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 *  See the License for the specific language governing permissions and
 *  limitations under the License.
 */

using System;
using System.Linq;
using Itinero.Algorithms.Networks;
using Itinero.Algorithms.Search.Hilbert;
using Itinero.Data;
using Itinero.IO.Osm.Tiles.Parsers;
using Itinero.LocalGeo;
using Itinero.Profiles;

namespace Itinero.IO.Osm.Tiles
{
    /// <summary>
    /// Contains extensions method for the routerdb.
    /// </summary>
    public static class RouterDbExtensions
    {
        /// <summary>
        /// The default zoom level to fetch tiles at.
        /// </summary>
        internal static int Zoom = 14;

        /// <summary>
        /// Loads all OSM data in the given bounding box by using routable tiles.
        /// </summary>
        /// <param name="db">The routerdb to fill.</param>
        /// <param name="box">The bounding box to fetch tiles for.</param>
        /// <param name="keepGlobalIds">Flag to keep the global ids.</param>
        /// <param name="baseUrl">The base url of the routeable tile source.</param>
        /// <param name="vehicleCache">The vehicle cache to use.</param>
        public static void LoadOsmDataFromTiles(this RouterDb db, Box box, string baseUrl = TileParser.BaseUrl, bool keepGlobalIds = true, VehicleCache vehicleCache = null)
        {
            // build the tile range.
            var tileRange = new TileRange(box, Zoom);
            
            // build the vehicle cache.
            if (vehicleCache == null) vehicleCache = new VehicleCache(db.GetSupportedVehicles().ToArray());

            // get all the tiles and build the routerdb.
            var globalIdMap = db.ExtractGlobalIds();
            db.Network.GeometricGraph.Graph.MarkAsMulti(); // when loading data we need a multigraph.
            foreach (var tile in tileRange)
            {
                db.AddOsmTile(globalIdMap, tile, vehicleCache, baseUrl);
            }
            
            // keep global ids if it's a requirement.
            if (keepGlobalIds)
            {
                db.AddOrUpdateGlobalIds(globalIdMap);
            }

            // sort the network.
            db.Sort();

            // optimize the network by applying simplifications.
            db.OptimizeNetwork();

            // compress the network.
            db.Compress();
        }

        /// <summary>
        /// Adds or updates the global id data on the given routerdb with what's in the given map.
        /// </summary>
        /// <param name="db">The routerdb.</param>
        /// <param name="globalIdMap">The global id map.</param>
        internal static void AddOrUpdateGlobalIds(this RouterDb db, GlobalIdMap globalIdMap)
        {
            if (!db.VertexData.TryGet(Constants.GLOBAL_ID_META_NAME, out MetaCollection<long> globalIds))
            { // get or add the global ids vertex data.
                globalIds = db.VertexData.AddInt64(Constants.GLOBAL_ID_META_NAME);
            }
            
            // set all to empty.
            for (uint v = 0; v < db.Network.VertexCount; v++)
            {
                globalIds[v] = Constants.GLOBAL_ID_EMPTY;
            }
            
            // add the entries that exist.
            foreach (var pair in globalIdMap)
            {
                globalIds[pair.vertex] = pair.globalId;
            }
        }

        /// <summary>
        /// Extracts the global ids map, if any.
        /// </summary>
        /// <param name="db">The routerdb.</param>
        /// <returns>The global ids map filled with any data already there.</returns>
        internal static GlobalIdMap ExtractGlobalIds(this RouterDb db)
        {
            var globalIdMap = new GlobalIdMap();

            if (!db.VertexData.TryGet(Constants.GLOBAL_ID_META_NAME, out MetaCollection<long> globalIds))
            {
                return globalIdMap;
            }
            
            // set all non-empty.
            for (uint v = 0; v < db.Network.VertexCount; v++)
            {
                var globalId = globalIds[v];
                if (globalId == Constants.GLOBAL_ID_EMPTY) continue;

                globalIdMap.Set(globalId, v);
            }

            return globalIdMap;
        }
    }
}