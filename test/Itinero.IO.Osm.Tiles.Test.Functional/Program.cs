using System;
using System.Linq;
using Itinero.IO.Shape;
using Itinero.LocalGeo;

namespace Itinero.IO.Osm.Tiles.Test.Functional
{
    class Program
    {
        static void Main(string[] args)
        {
            Itinero.Logging.Logger.LogAction = (o, level, message, parameters) =>
            {
                Console.WriteLine(string.Format("[{0}] {1} - {2}", o, level, message));
            };
            
            // create a routerdb.
            var routerDb = new RouterDb();
            
            // specify what vehicles it should support.
            routerDb.AddSupportedVehicle(Itinero.Osm.Vehicles.Vehicle.Car);
            
            // start loading tiles.
            routerDb.LoadOsmDataFromTiles(new Box(51.179773424875634f, 4.5366668701171875f,
                51.29885215199866f, 4.8017120361328125f));
            
            // write as shapefile for testing.
            routerDb.WriteToShape("shapefile", routerDb.GetSupportedProfiles().ToArray());
        }
    }
}