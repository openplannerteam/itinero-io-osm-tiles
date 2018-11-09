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

using System.Collections;
using System.Collections.Generic;

namespace Itinero.IO.Osm.Tiles
{
    /// <summary>
    /// A data structure to keep mappings between global vertex ids and vertices.
    /// </summary>
    internal class GlobalIdMap : IEnumerable<(long globalId, uint vertex)>
    {
        private readonly Dictionary<long, uint> _vertexPerId = new Dictionary<long, uint>();

        /// <summary>
        /// Sets a new mapping.
        /// </summary>
        /// <param name="globalVertexId">The global vertex id.</param>
        /// <param name="vertex">The local vertex.</param>
        public void Set(long globalVertexId, uint vertex)
        {
            _vertexPerId[globalVertexId] = vertex;
        }

        /// <summary>
        /// Gets a mapping if it exists.
        /// </summary>
        /// <param name="globalVertexId">The global vertex id.</param>
        /// <param name="vertex">The vertex associated with the given global vertex, if any.</param>
        /// <returns>True if a mapping exists, false otherwise.</returns>
        public bool TryGet(long globalVertexId, out uint vertex)
        {
            return _vertexPerId.TryGetValue(globalVertexId, out vertex);
        }

        public IEnumerator<(long globalId, uint vertex)> GetEnumerator()
        {
            foreach (var pair in _vertexPerId)
            {
                yield return (pair.Key, pair.Value);
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}