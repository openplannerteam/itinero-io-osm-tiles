using System;
using System.IO;
using System.Linq;
using Itinero.IO.Osm.Tiles.Parsers;
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
            
            // do some local caching.
            TileParser.DownloadFunc = DownloadHelper.Download;
            
            // create a router db.
            var routerDb = new RouterDb();
            
            // specify what vehicles it should support.
            routerDb.AddSupportedVehicle(Itinero.Osm.Vehicles.Vehicle.Car);
            
            // start loading tiles.
            routerDb.LoadOsmDataFromTiles(new Box(50.25f, 3.8f,
                51.42f, 6.16f));
            
            // write as shape file for testing.
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