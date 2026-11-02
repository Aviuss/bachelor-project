namespace Agent;

class Program
{
    static void Main(string[] args)
    {
        var run = new Runner();
        run.run();
    }
}


class Runner
{
    public Runner()
    {
        Console.WriteLine("Hello runner!");
    }

    public void run()
    {
        Console.WriteLine("Hello runner!");
    }


}
