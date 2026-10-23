
using System;
using System.Linq;
using System.Web.Mvc;
using VirtualBusinessIncubation.Models;
using VirtualBusinessIncubation.Models.RozgarHubTableAdapters;

namespace VirtualBusinessIncubation.Controllers
{
    public class HomeController : Controller
    {
        public ActionResult Index(string search = "", int? categoryId = null)
        {
            RozgarHub ds = new RozgarHub();

            // Get data using TableAdapters
            new categoriesTableAdapter().Fill(ds.categories);
            new profile_gigsTableAdapter().Fill(ds.profile_gigs);

            search = (search ?? "").Trim();

            // Search and filter services
            var gigs = ds.profile_gigs.Where(g =>
                g.availability_status == "available" &&
                (categoryId == null || g.category_id == categoryId) &&
                (search == "" ||
                 g.title.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                 (!g.IsdescriptionNull() &&
                  g.description.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0) ||
                 ds.categories.Any(c => c.id == g.category_id &&
                  c.name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0))
            ).OrderByDescending(g => g.average_rating).ToList();

            ViewBag.SearchTerm = search;
            ViewBag.SelectedCategoryId = categoryId;
            ViewBag.MatchingServiceCount = gigs.Count;
            ViewBag.DataUnavailable = false;

            // Show only 12 services
            gigs = gigs.Take(12).ToList();

            foreach (var gig in ds.profile_gigs.ToList())
            {
                if (!gigs.Contains(gig))
                    ds.profile_gigs.Rows.Remove(gig);
            }

            return View(ds);
        }

        public ActionResult About()
        {
            return View();
        }

        public ActionResult Contact()
        {
            return View();
        }
    }
}
