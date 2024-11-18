namespace Ultra.Example;

/// <summary>
/// Sample program using Markdig and Scriban to create a workload example for profiling with ultra 
/// </summary>
internal class Program
{
    static async Task Main(string[] args)
    {
        const int CountBenchMarkdig = 500;
        const int CountBenchScriban = 5000;

        var md = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "CommonMark.md"));

        var benchMarkdig = () =>
        {
            for (int i = 0; i < CountBenchMarkdig; i++)
            {
                var html = Markdig.Markdown.ToHtml(md);
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
            for (int i = 0; i < CountBenchScriban; i++)
            {
                var text = template.Render(new { values = values });
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
    }
}