using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Open : MonoBehaviour
{
    private Animator mAnimator;

    void Start()
    {
        mAnimator = GetComponent<Animator>();
    }

    void Update()
    {
        if(mAnimator != null)
        {
            if(Input.GetKeyDown(KeyCode.O))
            {
                Debug.Log("O key");
                mAnimator.SetTrigger("TrOpen");
            }
        }
    }
}
