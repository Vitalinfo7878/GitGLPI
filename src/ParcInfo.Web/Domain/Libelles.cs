using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Reflection;

namespace ParcInfo.Web.Domain;

public static class Libelles
{
    private static readonly ConcurrentDictionary<Enum, string> Cache = new();

    /// <summary>Libellé français d'une valeur d'énumération (attribut [Display]).</summary>
    public static string Libelle(this Enum valeur) => Cache.GetOrAdd(valeur, v =>
        v.GetType().GetField(v.ToString())?.GetCustomAttribute<DisplayAttribute>()?.Name ?? v.ToString());

    /// <summary>Description d'une valeur d'énumération (attribut [Display(Description = …)]).</summary>
    public static string? Description(this Enum valeur) =>
        valeur.GetType().GetField(valeur.ToString())?.GetCustomAttribute<DisplayAttribute>()?.Description;
}
