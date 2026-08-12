using SIL.Motif.Contract.Ids;
using SIL.LCModel;

namespace SIL.Motif.Runner.Resolution;

/// <summary>
/// Resolves a <see cref="CanonicalId"/> (the <c>FromGuid</c> form, for an existing object) to the
/// live <see cref="ICmObject"/> it names, via the public identity map. <see cref="CanonicalId.ToGuid"/>
/// is the exact inverse of <see cref="CanonicalId.FromGuid"/>, so this recovers precisely the
/// <c>Guid</c> the id was built from and looks it up as LibLCM's own storage key — none of the
/// GUID-realization or collision handling that applies to a proposed, not-yet-existing entity id is
/// involved here.
/// </summary>
public static class CanonicalIdResolver
{
    /// <remarks>
    /// Throws whatever <c>ICmObjectRepository.GetObject(Guid)</c> throws when no object with this
    /// identity exists in <paramref name="cache"/> — this method does no additional preflight or
    /// existence check of its own.
    /// </remarks>
    public static ICmObject Resolve(LcmCache cache, CanonicalId target)
    {
        var repository = cache.ServiceLocator.GetInstance<ICmObjectRepository>();
        return repository.GetObject(target.ToGuid());
    }
}
