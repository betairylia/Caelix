using UnityEngine;

namespace Caelix
{
    /// <summary>
    /// One enabled scene component per closed type. Lookup never creates objects or initializes
    /// resources, and ownership never implies DontDestroyOnLoad. Unity main thread only.
    /// </summary>
    /// <remarks>
    /// Override the Singleton hooks, not Unity's Awake/OnEnable/OnDisable/OnDestroy messages.
    /// Disabled owners retain their resources until destruction; they must reacquire ownership
    /// on enable. Rejected duplicates are disabled and can be explicitly enabled later.
    /// </remarks>
    public abstract class MonoSingleton<T> : MonoBehaviour where T : MonoSingleton<T>
    {
        private static T current;
        private bool destroyed;

        /// <summary>Existing enabled instance, or null. Must be called after instance OnEnable.</summary>
        public static T Current
        {
            get
            {
                if (current != null && current.isActiveAndEnabled && !current.destroyed)
                    return current;

                current = null;
                foreach (T candidate in FindObjectsByType<T>())
                {
                    if (!candidate.isActiveAndEnabled || candidate.destroyed) continue;
                    current = candidate;
                    break;
                }
                return current;
            }
        }

        /// <summary>Whether this enabled component holds the slot; does not search the scene.</summary>
        protected bool IsCurrent => !destroyed && isActiveAndEnabled && current == this;

        /// <summary>Claim before allocating resources. A duplicate is disabled without destruction.</summary>
        protected bool TryClaimSingleton()
        {
            if (destroyed || !isActiveAndEnabled) return false;
            if (current != null && (!current.isActiveAndEnabled || current.destroyed)) current = null;
            if (current == null) current = (T)this;
            if (current == this) return true;

            Debug.LogError($"{name}: another {typeof(T).Name} is enabled ({current.name}); disabling this component.", this);
            enabled = false;
            return false;
        }

        protected void Awake()
        {
            if (TryClaimSingleton()) OnSingletonEnabled();
        }

        protected void OnEnable()
        {
            if (TryClaimSingleton()) OnSingletonEnabled();
        }

        protected void OnDisable()
        {
            if (current == this) current = null;
            OnSingletonDisabled();
        }

        protected void OnDestroy()
        {
            destroyed = true;
            if (current == this) current = null;
            OnSingletonDestroyed();
        }

        /// <summary>May run from both Awake and OnEnable; initialization must be idempotent.</summary>
        protected virtual void OnSingletonEnabled() { }
        protected virtual void OnSingletonDisabled() { }
        /// <summary>Release owned resources, including any allocated before initialization failed.</summary>
        protected virtual void OnSingletonDestroyed() { }
    }
}
