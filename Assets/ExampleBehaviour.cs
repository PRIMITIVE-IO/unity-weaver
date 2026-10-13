using DefaultNamespace;
using UnityEngine;

public class ExampleBehaviour : MonoBehaviour
{
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
    
    void Start()
    {
        Other();
        Other2();
        ExampleClass exampleClass = new();

        var x = 1;
    }
}
