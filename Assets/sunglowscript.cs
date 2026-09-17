using UnityEngine;

public class sunglowscript : MonoBehaviour
{
    void Start()
    {
        ParticleSystem ps = GetComponent<ParticleSystem>();

        if (ps != null)
        {
            ps.Emit(1);
        }
    }
}
