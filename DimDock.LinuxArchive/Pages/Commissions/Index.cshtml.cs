using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DimDock.LinuxArchive.Pages.Commissions
{
    public class Tier
    {
        public int Id;
        public string Title;
        public string Description;
        public List<string> ExampleUrls = new List<string>();
        public string Pricing;
        public string Turnaround;
        public string Notes;
        public string PayWhen;
        public bool Queued;
        public bool Accepting;
    }

    public class IndexModel : PageModel
    {
        public List<Tier> Tiers;

        public IndexModel()
        {
            Tiers = new List<Tier>();

            var tier0 = new Tier()
            {
                Id = 0,
                Title = "Tier 0",
                Description = "Give me a prompt and you get what you get.",
                Pricing = "$15 Flat Fee",
                Turnaround = "1 week from acceptance & payment.",
                PayWhen = "Up Front",
                Queued = false,
                Accepting = true,
                Notes = @"
Promising a baseline level of effort, you're not going to get a shitty scribble but I might not refine the drawing.
If you're asking for something more complicated on this tier expect rougher results.
".Trim(),
            };

            var tier1 = new Tier()
            {
                Id = 1,
                Title = "Tier 1",
                Description = "Reasonably clean sketch.",
                Pricing = "$20-$40",
                Turnaround = "1 week from acceptance & payment.",
                PayWhen = "Up Front",
                Queued = false,
                Accepting = true,
                Notes = @"
It'll be like the personal sketches I post, most likely.
Clean, but not promising line art.
May do some shading.
".Trim(),
            };

            var tier2 = new Tier()
            {
                Id = 2,
                Title = "Tier 2",
                Description = "Something better than a sketch.",
                Pricing = "$40-$60",
                Turnaround = "1-2 weeks from start.",
                PayWhen = "Up Front",
                Queued = true,
                Accepting = true,
                Notes = @"
This might be a clean blue-tone drawing, or something with quick but decent shading/coloring.
Probably on the simpler side, complexity wise, e.g.
	Single character.
	Simple or no background.
	Busts.
".Trim(),
            };

            var tier3 = new Tier()
            {
                Id = 3,
                Title = "Tier 3",
                Description = "Like Tier 2, but more.",
                Pricing = "$60-$120",
                Turnaround = "1-4 weeks from start.",
                PayWhen = "Half up front, half on completion.",
                Queued = true,
                Accepting = true,
                Notes = @"
Rendering similar to tier 2, but more complicated or cleaner.
    Multiple characters, or multiple angles of the same character.
    Detailed background elements.
".Trim(),
            };

            var tier4 = new Tier()
            {
                Id = 4,
                Title = "Tier 4",
                Description = "High effort.",
                Pricing = "Depends",
                Turnaround = "Depends",
                PayWhen = "Half after sketch is provided & approved, half on completion.",
                Queued = true,
                Accepting = false,
                Notes = @"
<h4>Anything that falls in the high level of effort category, including but not limited to:</h4>
<ul>
	<li>Paintings.</li>
    <li>Clean line art.</li>
	<li>Colored and shaded drawings with clean line art.</li>
</ul>
<h4>Price:</h4>
<ul>
    <li>You could want a painting but a simple one, that might be cheaper than having me color clean line art.</li>
    <li>All depends on what you're looking for. In general, expect 100$+ if it's a reasonably complicated ask.</li>
</ul>
<h4>Turnaround:</h4>
<ul>
    <li>These are big time commitments.</li>
    <li>I ask that you have a timeframe in mind when you approach me asking for this type of work.</li>
    <li>I work better with concrete deadlines.</li>
    <li>I can provide a ballpark estimate for time to complete but there have been occasions where I've taken months to complete complicated works.</li>
    <li>I work a full-time job and can't always dedicate all of my free time to drawing, so please keep this in mind and level your expectations accordingly.</li>
</ul>
".Trim(),
            };

            tier0.ExampleUrls.Add("/img/examples/t0_01.png");
            tier0.ExampleUrls.Add("/img/examples/t0_02.png");
            tier0.ExampleUrls.Add("/img/examples/t0_03.png");
            tier1.ExampleUrls.Add("/img/examples/t1_01.png");
            tier1.ExampleUrls.Add("/img/examples/t1_02.png");
            tier1.ExampleUrls.Add("/img/examples/t1_03.png");
            tier2.ExampleUrls.Add("/img/examples/t2_01.png");
            tier2.ExampleUrls.Add("/img/examples/t2_02.png");
            tier2.ExampleUrls.Add("/img/examples/t2_03.png");
            tier3.ExampleUrls.Add("/img/examples/t3_01.png");
            tier3.ExampleUrls.Add("/img/examples/t3_02.png");
            tier3.ExampleUrls.Add("/img/examples/t3_03.png");
            tier4.ExampleUrls.Add("/img/examples/t4_01.png");
            tier4.ExampleUrls.Add("/img/examples/t4_02.png");
            tier4.ExampleUrls.Add("/img/examples/t4_03.png");

            Tiers.Add(tier0);
            Tiers.Add(tier1);
            Tiers.Add(tier2);
            Tiers.Add(tier3);
            Tiers.Add(tier4);

        }

        public void OnGet()
        {
        }
    }
}
