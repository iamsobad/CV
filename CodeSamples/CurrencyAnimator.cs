using DG.Tweening;
using Sirenix.OdinInspector;
using System.Collections.Generic;
using System.Drawing;
using UnityEngine;
using UnityEngine.Pool;
using UnityEngine.U2D;
using UnityEngine.UI;

public class CurrencyAnimator : MonoBehaviour
{
    public static CurrencyAnimator Instance;

    [SerializeField]
    private UIHelper uIHelper;

    [SerializeField]
    private Transform energyTransform;
    [SerializeField]
    private Transform coinsTransform;
    [SerializeField]
    private Transform heartsTransform;
    [SerializeField]
    private Transform crystalsTransform;

    [SerializeField, BoxGroup("Size and Amount")]
    private Vector2 defaultImagesSize = new Vector2(64, 64);
    [SerializeField, BoxGroup("Size and Amount")]
    private int defaultCount = 25;

    [SerializeField, BoxGroup("Start")]
    private float timeToSpawn = .5f;
    [SerializeField, BoxGroup("Start"), Range(0, 1), SuffixLabel("*100% (in percents)")]
    private float maxSpawnDelay = .6f;
    [SerializeField, BoxGroup("Start")]
    private float radius = 200;
    [SerializeField, BoxGroup("Start"), Range(0, 1), SuffixLabel("*100% (in percents)")]
    private float minRadiusPercent = .3f;

    [SerializeField, BoxGroup("Flight")]
    private float timeToFly = 1f;
    [SerializeField, BoxGroup("Flight")]
    private float maxXDeviation = 300;
    [SerializeField, BoxGroup("Flight")]
    private float maxYDeviation = 100;
    [SerializeField, BoxGroup("Flight")]
    private int curvePointsCount = 5;

    private Sequence seq;
    private ObjectPool<GameObject> pool;

    private void Awake()
    {
        Instance = this;
        pool = new ObjectPool<GameObject>(CreateParticle, GetParticle, ReleaseParticle);
    }

    public void ShowHeartsReward(List<Image> images, System.Action startAction = null, System.Action perParticleEnd = null)
        => AnimateCurrency(images, heartsTransform.position, startAction, perParticleEnd);

    public void ShowCoinsReward(List<Image> images, System.Action startAction = null, System.Action perParticleEnd = null)
        => AnimateCurrency(images, coinsTransform.position, startAction, perParticleEnd);

    private void AnimateCurrency(List<Image> images, Vector3 endPosition, System.Action startAction = null, System.Action perParticleEnd = null)
    {
        //if (seq != null)
        //    seq.Kill(true);

        seq = DOTween.Sequence();

        Image p_Image;
        Vector3[] curvePoints;
        float[] startTimes = new float[images.Count];

        bool deviationDirection = Random.value > .5f;

        for (int i = 0; i < images.Count; i++)
        {
            if (!images[i].gameObject.activeSelf)
                continue;

            GameObject p_GameObject = pool.Get();
            p_GameObject.transform.position = images[i].transform.position;
            p_GameObject.transform.localScale = Vector3.one;

            p_Image = p_GameObject.GetComponent<Image>();
            p_Image.sprite = images[i].sprite;
            p_Image.rectTransform.sizeDelta = images[i].rectTransform.sizeDelta;

            startTimes[i] = Random.Range(0, timeToSpawn * maxSpawnDelay);

            seq.Insert(startTimes[i], p_GameObject.transform.DOScale(new Vector3(1.1f, 1.1f, 1.1f), timeToSpawn / 2));
            seq.Insert(startTimes[i] + timeToSpawn / 2, p_GameObject.transform.DOScale(Vector3.one, timeToSpawn / 2));

            curvePoints = GetPointsOfArc(images[i].transform.position, endPosition, curvePointsCount, new Vector3(Random.Range(0, deviationDirection ? maxXDeviation : -maxXDeviation), Random.Range(-maxYDeviation, maxYDeviation), 0));
            float timeToPointFly = timeToFly / curvePoints.Length;

            for (int k = 0; k < curvePoints.Length; k++)
                seq.Insert(startTimes[i] + timeToSpawn + k * timeToPointFly, p_GameObject.transform.DOMove(curvePoints[k], timeToPointFly).SetEase(Ease.Linear));

            seq.InsertCallback(startTimes[i] + timeToSpawn + timeToFly, () =>
            {
                pool.Release(p_GameObject);
                perParticleEnd?.Invoke();
            });
        }

        seq.OnComplete(OnAnimationComplete);
        startAction?.Invoke();
    }

    public void ShowCurrency(Resource resource, Vector3 startPosition, System.Action startAction = null)
    {
        Resource rewardResource = new Resource(resource);
        int particlesCount = Mathf.Min(defaultCount, rewardResource.IntAmount);
        rewardResource.Amount /= particlesCount;
        ShowCurrency(rewardResource.Type, startPosition, particlesCount, startAction, () =>
        {
            ResourceHolder.Instance.Add(rewardResource);
        });
    }

    public void ShowCurrency(ResourceType resourceType, Vector3 startPosition, int count, System.Action startAction = null, System.Action perParticleEnd = null)
    {
        switch (resourceType)
        {
            case ResourceType.Energy: ShowEnergy(startPosition, count, startAction, perParticleEnd); break;
            case ResourceType.Coins: ShowCoins(startPosition, count, startAction, perParticleEnd); break;
            case ResourceType.Hearts: ShowHearts(startPosition, count, startAction, perParticleEnd); break;
            case ResourceType.Crystals: ShowCrystals(startPosition, count, startAction, perParticleEnd); break;
            default:
                Debug.LogError($"Cant find animation method for {resourceType}");
                break;
        }
    }

    public void ShowEnergy(Vector3 startPosition, int count, System.Action startAction = null, System.Action perParticleEnd = null)
        => Show("Energy_icon", startPosition, energyTransform.position, count, defaultImagesSize, startAction, perParticleEnd);

    public void ShowCoins(Vector3 startPosition, int count, System.Action startAction = null, System.Action perParticleEnd = null)
        => Show("Coins_icon", startPosition, coinsTransform.position, count, defaultImagesSize, startAction, perParticleEnd);

    public void ShowHearts(Vector3 startPosition, int count, System.Action startAction = null, System.Action perParticleEnd = null)
        => Show("Hearts_icon", startPosition, heartsTransform.position, count, defaultImagesSize, startAction, perParticleEnd);

    public void ShowCrystals(Vector3 startPosition, int count, System.Action startAction = null, System.Action perParticleEnd = null)
        => Show("Crystals_icon", startPosition, crystalsTransform.position, count, defaultImagesSize, startAction, perParticleEnd);

    public void Show(string spriteName, Vector3 startPosition, Vector3 endPosition, int count, Vector2 size, System.Action startAction = null, System.Action perParticleEnd = null)
        => Show(uIHelper.GetSprite(spriteName), startPosition, endPosition, count, size, startAction, perParticleEnd);

    public void Show(Sprite sprite, Vector3 startPosition, Vector3 endPosition, int count, Vector2 size, System.Action startAction = null, System.Action perParticleEnd = null)
    {
        //if (seq != null)
        //    seq.Kill(true);

        seq = DOTween.Sequence();
       
        Image p_Image;
        Vector3 circlePosition;
        Vector3[] curvePoints;
        float[] startTimes = new float[count];
        //visibleParticles = new GameObject[count];

        bool deviationDirection = Random.value > .5f;

        for (int i = 0; i < count; i++)
        {
            GameObject p_GameObject = pool.Get();
            p_GameObject.transform.position = startPosition;
            //visibleParticles[i] = p_GameObject;

            p_Image = p_GameObject.GetComponent<Image>();
            p_Image.sprite = sprite;
            p_Image.rectTransform.sizeDelta = Vector3.zero;

            startTimes[i] = Random.Range(0, timeToSpawn * maxSpawnDelay);
            circlePosition = GetCirclePosition(startPosition);

            seq.Insert(startTimes[i], p_Image.rectTransform.DOSizeDelta(size, timeToSpawn));
            seq.Insert(startTimes[i], p_GameObject.transform.DOMove(circlePosition, timeToSpawn).SetEase(Ease.InOutSine));
            seq.Insert(startTimes[i], p_Image.DOAlpha(0, 1, timeToSpawn));

            curvePoints = GetPointsOfArc(circlePosition, endPosition, curvePointsCount, new Vector3(Random.Range(0, deviationDirection ? maxXDeviation : -maxXDeviation), Random.Range(-maxYDeviation, maxYDeviation), 0));
            float timeToPointFly = timeToFly / curvePoints.Length;

            for (int k = 0; k < curvePoints.Length; k++)
                seq.Insert(startTimes[i] + timeToSpawn + k * timeToPointFly, p_GameObject.transform.DOMove(curvePoints[k], timeToPointFly).SetEase(Ease.Linear));

            seq.InsertCallback(startTimes[i] + timeToSpawn + timeToFly, () =>
                {
                    pool.Release(p_GameObject);
                    perParticleEnd?.Invoke();
                });
        }

        seq.OnComplete(OnAnimationComplete);
        startAction?.Invoke();
    }

    private void OnAnimationComplete()
    {
        seq = null;
    }

    #region math
    private Vector3 GetCirclePosition(Vector3 startPosition)
    {
        Vector3 result = Quaternion.AngleAxis(Random.Range(0, 360), Vector3.forward) * Vector3.right * Random.Range(radius * minRadiusPercent, radius) + startPosition;
        result.z = startPosition.z;
        return result;
    }

    private Vector3[] GetPointsOfArc(Vector3 startPosition, Vector3 endPosition, int pointsNum, Vector3 middlePointOffset)
    {
        Vector3[] result = new Vector3[pointsNum + 1];

        Vector3 middlePoint = new Vector3(((startPosition.x + endPosition.x) / 2) + middlePointOffset.x, ((startPosition.y + endPosition.y) / 2) + middlePointOffset.y, ((startPosition.z + endPosition.z) / 2) + middlePointOffset.z);

        for (int i = 0; i <= pointsNum; i++)
            result[i] = GetPointOfBezierCurveByIndex(i, startPosition, endPosition, middlePoint, pointsNum);

        return result;
    }

    private Vector3 GetPointOfBezierCurveByIndex(int i, Vector3 startPosition, Vector3 endPosition, Vector3 middlePoint, int pointsNum)
    {
        float t = i * (1f / pointsNum);
        return ((1 - t) * (1 - t) * startPosition) + (2 * t * (1 - t) * middlePoint) + (t * t * endPosition);
    }
    #endregion

    #region Pool methods
    private GameObject CreateParticle()
    {
        GameObject result = new GameObject($"Particle_{pool.CountAll}");
        result.transform.parent = transform;
        result.transform.localScale = Vector3.one;
        Image img = result.AddComponent<Image>();
        img.raycastTarget = false;
        return result;
    }

    private void GetParticle(GameObject go) => go.SetActive(true);

    private void ReleaseParticle(GameObject go) => go.SetActive(false);
    #endregion
}
