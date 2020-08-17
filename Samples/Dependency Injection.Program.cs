using System;

namespace MyApp
{
    class Program
    {
        static void Main()
        {
            // var foo = DI.ServiceLocator.GetService<IFoo>();

            // var anotherFoo = DI.ServiceLocator.GetService<IFoo>();

            // var bar = DI.ServiceLocator.GetService<IBar>();

            // var baz = DI.ServiceLocator.GetService<IBaz>();

            Console.WriteLine("Hello World");
        }
    }

    interface IFoo
    {
    }

    class Foo : IFoo
    {
    }

    interface IBar
    {
    }

    class Bar : IBar
    {
    }

    interface IBaz
    {
    }
}
