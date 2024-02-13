namespace DefaultNamespace
{
    public class ExampleClass
    {
        public ExampleClass()
        {
            Other3();
            Other2();
            var x = GetNumber1();
            InnerClass innerClass = new();
            innerClass.GetA();
        }
        
        void Other3()
        {
            long count = 0;
            for (int ii = 0; ii < 100000000; ii++)
            {
                count++;
            }
        }
        
        void Other2()
        {
            long count = 0;
            for (int ii = 0; ii < 100000000; ii++)
            {
                count++;
            }
        }

        static int GetNumber1()
        {
            return 1;
        }

        class InnerClass
        {
            public string GetA()
            {
                return "A";
            }
            
            string GetB()
            {
                return "A";
            }
        }
    }
}