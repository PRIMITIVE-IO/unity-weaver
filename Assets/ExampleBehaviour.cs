using UnityEngine;
using Weaver;

public class ExampleBehaviour : MonoBehaviour
{
    float m_Height = 0f;

    //[OnChanged("OnHeightChanged")]
    public float height
    {
        get => m_Height;
        set => m_Height = value;
    }

    //[OnChanged("OnAgeChanged", isValidated = true)]
    public int age { get; set; }

    public int otherAge
    {
        get => age;
        set
        {
            if (age != value)
            {
                OnAgeChanged(value);
            }
        }
    }
    
    [ProfileSample]
    public void Other()
    {
        long count = 0;
        for (int ii = 0; ii < 100000000; ii++)
        {
            count++;
        }
    }

    public string text()
    {
        return "some text";
    }

    [ProfileSample]
    public void Other2()
    {
        long count = 0;
        for (int ii = 0; ii < 100000000; ii++)
        {
            count++;
        }
    }
    
    
    [ProfileSample]
    public void Start()
    {
        age = 23;
        height = 6.1f;
        Other();
        Other2();
        gameObject.GetInstanceID();
    }

    void OnHeightChanged(float newHeight)
    {
        Debug.Log("Height changed from " + m_Height + " to " + newHeight);
    }

    void OnAgeChanged(int newAge)
    {
        Debug.Log("Age changed from " + age + " to " + newAge);
    }

    void OnValidate()
    {

    }
}
