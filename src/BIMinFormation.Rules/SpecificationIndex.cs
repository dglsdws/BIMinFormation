using System;
using System.Collections.Generic;
using System.Linq;
using BIMinFormation.Ids;

namespace BIMinFormation.Rules
{
    /// <summary>
    /// Index of the specifications by a required applicability condition, so as not to test every specification on every
    /// element (210 specifications × 10 thousand elements = 2.1 million tests). Per specification, the first simple
    /// condition that fits: entity with an exact name (the IDS does not accept subtypes), or a property with exact set, name
    /// and text value (e.g. <c>BIMPR.EOI = PR.11.05 PAREDE</c>). A specification without a simple condition is a candidate
    /// for every element. The index only drops what has no chance: the full test still runs through
    /// <see cref="SpecificationEvaluator.IsApplicable"/>.
    /// </summary>
    public sealed class SpecificationIndex
    {
        private readonly IReadOnlyList<Specification> _specifications;
        private readonly List<int> _always = new List<int>();
        private readonly Dictionary<string, List<int>> _byEntity = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(string Set, string Name, string Value), List<int>> _byProperty =
            new Dictionary<(string, string, string), List<int>>();

        private readonly HashSet<(string Set, string Name)> _indexedProperties = new HashSet<(string, string)>();

        public SpecificationIndex(IReadOnlyList<Specification> specifications)
        {
            _specifications = specifications;
            for (int i = 0; i < specifications.Count; i++)
            {
                var facets = specifications[i].Applicability.Facets;
                var entity = facets.OfType<EntityFacet>().FirstOrDefault(f => f.Name is SimpleValue);
                if (entity != null)
                {
                    Add(_byEntity, ((SimpleValue)entity.Name).Text, i);
                    continue;
                }

                var property = facets.OfType<PropertyFacet>().FirstOrDefault(f => f.Cardinality == FacetCardinality.Required
                    && f.PropertySet is SimpleValue && f.BaseName is SimpleValue && f.Value is SimpleValue);
                if (property != null)
                {
                    var key = (((SimpleValue)property.PropertySet).Text, ((SimpleValue)property.BaseName).Text, ((SimpleValue)property.Value!).Text);
                    Add(_byProperty, key, i);
                    _indexedProperties.Add((key.Item1, key.Item2));
                    continue;
                }

                _always.Add(i);
            }
        }

        private static void Add<TKey>(Dictionary<TKey, List<int>> index, TKey key, int position) where TKey : notnull
        {
            if (!index.TryGetValue(key, out var list))
            {
                list = new List<int>();
                index[key] = list;
            }

            list.Add(position);
        }

        /// <summary>Specifications that may apply to the element, in the original order (before the full test).</summary>
        public IEnumerable<Specification> Candidates(ElementState element)
        {
            var positions = new SortedSet<int>(_always);
            if (_byEntity.TryGetValue(element.Entity, out var byEntity)) positions.UnionWith(byEntity);
            // IFC2X3: the class also comes from the type (IfcFlowTerminal + IfcAirTerminalType = IfcAirTerminal)
            if (element.TypeEntity != null && _byEntity.TryGetValue(FacetEvaluator.OccurrenceClassOf(element.TypeEntity), out var byType))
            {
                positions.UnionWith(byType);
            }
            if (_byProperty.Count > 0)
            {
                foreach (var set in element.PropertySets)
                {
                    foreach (var property in set.Properties)
                    {
                        if (!_indexedProperties.Contains((set.Name, property.Name))) continue;
                        foreach (var value in property.Values)
                        {
                            // a value that is not text does not rule it out: the typed comparison is left to the full test
                            if (value.Kind != ValueKind.String)
                            {
                                foreach (var pair in _byProperty.Where(p => p.Key.Set == set.Name && p.Key.Name == property.Name))
                                {
                                    positions.UnionWith(pair.Value);
                                }
                            }
                            else if (_byProperty.TryGetValue((set.Name, property.Name, value.Text ?? ""), out var byValue))
                            {
                                positions.UnionWith(byValue);
                            }
                        }
                    }
                }
            }

            return positions.Select(p => _specifications[p]);
        }

        /// <summary>Specifications that apply to the element (candidates that pass the full test).</summary>
        public List<Specification> Applicable(ElementState element, ModelState model) =>
            Candidates(element).Where(s => SpecificationEvaluator.IsApplicable(s, element, model)).ToList();
    }
}
