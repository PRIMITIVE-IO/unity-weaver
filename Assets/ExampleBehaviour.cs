using DefaultNamespace;
using UnityEngine;
using Weaver.Attributes;

public class ExampleBehaviour : MonoBehaviour
{
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
    
    [ProfileSample]
    void Start()
    {
        Other();
        Other2();
        ExampleClass exampleClass = new ExampleClass();
    }
    
    string GetInstanceIDs()
    {
        return $"{GetInstanceID()}:{gameObject.GetInstanceID()}";
    }
}
