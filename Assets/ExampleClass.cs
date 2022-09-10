namespace DefaultNamespace
{
    public class ExampleClass
    {
        public ExampleClass()
        {
            Other();
            Other2();
        }
        
        void Other()
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
    }
}