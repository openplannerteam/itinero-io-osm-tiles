using System;
using System.IO;
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
                Console.WriteLine($"[{o}] {level} - {message}");
            };
            
            // create a routerdb.
            var routerDb = new RouterDb();
            
            // specify what vehicles it should support.
            routerDb.AddSupportedVehicle(Itinero.Osm.Vehicles.Vehicle.Car);
            
            // start loading tiles.
            routerDb.LoadOsmDataFromTiles(new Box(51.265271575597446f, 4.793086051940918f,
                51.24195743492624f, 4.748368263244629f));
            
            // write as shapefile for testing.
            routerDb.WriteToShape("shapefile", routerDb.GetSupportedProfiles().ToArray());
            
            // calculate route.
            var router = new Router(routerDb);
            var route = router.Calculate(routerDb.GetSupportedProfile("car"),
                new Coordinate(51.265271575597446f, 4.793086051940918f),
                new Coordinate(51.24195743492624f, 4.748368263244629f));
            File.WriteAllText("route.geojson", route.ToGeoJson());
        }
    }
}