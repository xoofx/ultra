// Sample program using Markdig and Scriban to create a workload example for profiling with ultra 

const int countBenchMarkdig = 500;
const int countBenchScriban = 5000;
var md = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "CommonMark.md"));

var benchMarkdig = () =>
{
    for (int i = 0; i < countBenchMarkdig; i++)
    {
        var html = Markdig.Markdown.ToHtml(md);
        if (i % 100 == 0 && i > 0)
        {
            Console.WriteLine($"Markdig {i} conversions done");
        }
    }
};

var benchScriban = () =>
{
    var template = Scriban.Template.Parse("""
                                          {{ for $i in values }}
                                          [{{i}}] This is an example of a template with a loop
                                          {{ end }}
                                          """);

    var values = new List<string> { "one", "two", "three" };
    for (int i = 0; i < countBenchScriban; i++)
    {
        var text = template.Render(new { values = values });

        if (i % 1000 == 0 && i > 0)
        {
            Console.WriteLine($"Scriban {i} conversions done");
        }
    }
};

benchMarkdig();

var tasks = new List<Task>();

for (int i = 0; i < Math.Max(2, Environment.ProcessorCount / 4); i++)
{
    var markdigTask = new Task(benchMarkdig);
    var scribanTask = new Task(benchScriban);

    markdigTask.Start();
    scribanTask.Start();

    tasks.Add(markdigTask);
    tasks.Add(scribanTask);
}

benchScriban();

await Task.WhenAll(tasks);