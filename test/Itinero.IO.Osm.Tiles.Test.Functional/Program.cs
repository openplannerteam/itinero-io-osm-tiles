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
            Logging.Logger.LogAction = (o, level, message, parameters) =>
            {
                Console.WriteLine($"[{o}] {level} - {message}");
            };
            
            // create a routerdb.
            var routerDb = new RouterDb();
            
            // specify what vehicles it should support.
            routerDb.AddSupportedVehicle(Itinero.Osm.Vehicles.Vehicle.Car);
            
            // start loading tiles.
            routerDb.LoadOsmDataFromTiles(new Box(50.865236286815914f, 4.6746826171875f,
                50.89253085119355f, 4.724636077880859f), baseUrl: "https://tiles.openplanner.team/staging");
            
            // write as shapefile for testing.
            routerDb.WriteToShape("shapefile", routerDb.GetSupportedProfiles().ToArray());
            
            // calculate route.
            var router = new Router(routerDb);
            var route = router.Calculate(routerDb.GetSupportedProfile("car"),
                new Coordinate(50.88638555338903f, 4.68390941619873f),
                new Coordinate(50.87390296000361f, 4.716954231262207f));
            File.WriteAllText("route.geojson", route.ToGeoJson());
        }
    }
}