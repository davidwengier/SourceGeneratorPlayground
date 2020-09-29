using System;

namespace ConsoleApp
{
    public class Program
    {
        public static void Main(string[] args)
        {
            DoSomethingSimple(Simple.Second);
            DoSomethingComplex(Complex.Fourth);

            // This one is invalid!
            DoSomethingComplex((Complex)5);
        }

        static void DoSomethingSimple(Simple simple)
        {
            EnumValidation.EnumValidator.Validate(simple);

            Console.WriteLine("Doing someting complex with " + simple);
        }

        static void DoSomethingComplex(Complex complex)
        {
            EnumValidation.EnumValidator.Validate(complex);

            Console.WriteLine("Doing someting complex with " + complex);
        }
    }
    
    enum Simple
    {
        First,
        Second
    }

    enum Complex
    {
        First = 3,
        Second = 4,
        Third = 7,
        Fourth = 8,
        Fifth = 9
    }
}
