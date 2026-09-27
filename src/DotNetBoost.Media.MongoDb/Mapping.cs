using DotNetBoost.Media.Core.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace DotNetBoost.Media.MongoDb;

/// <summary>
/// The class maps for <see cref="MediaFile"/> and <see cref="MediaAttachment"/>. Registered once,
/// the first time the store is configured — the driver's map registry is global and re-registering
/// a type throws.
/// </summary>
internal static class MediaFileClassMap
{
    // A plain object rather than System.Threading.Lock: that type arrived in .NET 9 and these
    // packages also target net8.0.
    private static readonly object Gate = new();
    private static bool _registered;

    public static void Register()
    {
        // Double-checked: this is called on every UseMongoDb(), which usually happens once but is
        // not guaranteed to, and the driver's registry is not forgiving of a second registration.
        if (_registered)
            return;

        lock (Gate)
        {
            if (_registered)
                return;

            if (!BsonClassMap.IsClassMapRegistered(typeof(MediaFile)))
            {
                BsonClassMap.RegisterClassMap<MediaFile>(map =>
                {
                    map.AutoMap();

                    // The file's own id is the document's _id: one unique index instead of two,
                    // and a lookup by id needs no secondary index at all.
                    map.MapIdMember(f => f.Id).SetSerializer(new GuidSerializer(GuidRepresentation.Standard));

                    // Computed from DeletedAt. Persisting it would add a field that can disagree
                    // with the one it is derived from.
                    map.UnmapMember(f => f.IsDeleted);

                    // Null-valued optional fields are left out of the document rather than stored
                    // as explicit nulls — smaller documents, and a sparse index can then be used
                    // for the ones that are usually absent.
                    foreach (var member in map.DeclaredMemberMaps)
                        member.SetIgnoreIfNull(true);
                });
            }

            if (!BsonClassMap.IsClassMapRegistered(typeof(MediaAttachment)))
            {
                BsonClassMap.RegisterClassMap<MediaAttachment>(map =>
                {
                    map.AutoMap();

                    // The link's own id is the document's _id, so removing one attachment is a
                    // primary-key delete.
                    map.MapIdMember(a => a.Id).SetSerializer(new GuidSerializer(GuidRepresentation.Standard));

                    // The file it points at is a Guid too, and the driver needs telling: without a
                    // serializer it would be written in the legacy binary subtype, which would not
                    // compare equal to the _id of the file document it names.
                    map.MapMember(a => a.MediaFileId).SetSerializer(new GuidSerializer(GuidRepresentation.Standard));

                    // Every field here is required and always written, so there is nothing to
                    // IgnoreIfNull — unlike the file document, whose optional fields are left out.
                });
            }

            _registered = true;
        }
    }
}
