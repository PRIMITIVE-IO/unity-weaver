using Weaver.Attributes;

namespace DefaultNamespace
{
    public class ExampleClass
    {
        [ProfileSample]
        public ExampleClass()
        {
            Other();
            Other2();
        }
        
        [ProfileSample]
        void Other()
        {
            long count = 0;
            for (int ii = 0; ii < 100000000; ii++)
            {
                count++;
            }
        }

        [ProfileSample]
        void Other2()
        {
            long count = 0;
            for (int ii = 0; ii < 100000000; ii++)
            {
                count++;
            }
        }
        
        string GetInstanceIDs()
        {
            return "-1";
        }
    }
}