using System;

namespace MyApp
{
    class Program
    {
        static void Main()
        {
            var foo = DI.ServiceLocator.GetService<IFoo>();
            Console.WriteLine(foo.Message());
        }
    }

    interface IFoo
    {
        string Message();
    }

    class Foo : IFoo
    {
        public string Message() => "Hello World";
    }
}