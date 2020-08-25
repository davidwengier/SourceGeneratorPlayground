using System;

namespace MyApp
{
    class Program
    {
        static void Main()
        {
            // Comment and uncomment these lines to see how the generation changes
            var foo = DI.ServiceLocator.GetService<IFoo>();

            var anotherFoo = DI.ServiceLocator.GetService<IFoo>();

            var bar = DI.ServiceLocator.GetService<IBar>();

            //// Uncomment to demonstrate build errors:
            // var baz = DI.ServiceLocator.GetService<IBaz>();

            Console.WriteLine("Hello World");
        }
    }

    //// Comment and uncomment the attribute to see how the generation changes
    //[DI.Transient]
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
