﻿namespace Microsoft.VisualStudio.Composition
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Composition;
    using System.Linq;
    using System.Reflection;
    using System.Text;
    using System.Threading.Tasks;
    using Validation;

    public class AttributedPartDiscovery : PartDiscovery
    {
        public override ComposablePartDefinition CreatePart(Type partType)
        {
            Requires.NotNull(partType, "partType");

            var exportsOnType = ImmutableList.CreateBuilder<ExportDefinition>();
            var exportsOnMembers = ImmutableDictionary.CreateBuilder<MemberInfo, ExportDefinition>();
            var imports = ImmutableDictionary.CreateBuilder<MemberInfo, ImportDefinition>();
            var exportMetadataOnType = GetExportMetadata(partType.GetCustomAttributes());

            foreach (var exportAttribute in partType.GetCustomAttributes<ExportAttribute>())
            {
                var partTypeAsGenericTypeDefinition = partType.IsGenericType ? partType.GetGenericTypeDefinition() : null;
                var contract = new CompositionContract(exportAttribute.ContractName, exportAttribute.ContractType ?? partTypeAsGenericTypeDefinition ?? partType);
                var exportDefinition = new ExportDefinition(contract, exportMetadataOnType);
                exportsOnType.Add(exportDefinition);
            }

            var sharedAttribute = partType.GetCustomAttribute<SharedAttribute>();
            string sharingBoundary = null;
            if (sharedAttribute != null)
            {
                sharingBoundary = sharedAttribute.SharingBoundary ?? string.Empty;
            }

            foreach (var member in partType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                var importAttribute = member.GetCustomAttribute<ImportAttribute>();
                var importManyAttribute = member.GetCustomAttribute<ImportManyAttribute>();
                var exportAttribute = member.GetCustomAttribute<ExportAttribute>();
                Requires.Argument(!(importAttribute != null && importManyAttribute != null), "partType", "Member \"{0}\" contains both ImportAttribute and ImportManyAttribute.", member.Name);
                Requires.Argument(!(exportAttribute != null && (importAttribute != null || importManyAttribute != null)), "partType", "Member \"{0}\" contains both import and export attributes.", member.Name);

                var importConstraints = GetImportConstraints(member.GetCustomAttributes<ImportMetadataConstraintAttribute>());
                ImportDefinition importDefinition;
                if (TryCreateImportDefinition(member.PropertyType, member.GetCustomAttributes(), importConstraints, out importDefinition))
                {
                    imports.Add(member, importDefinition);
                }
                else if (exportAttribute != null)
                {
                    Verify.Operation(!partType.IsGenericTypeDefinition, "Exports on members not allowed when the declaring type is generic.");
                    var exportMetadataOnMember = GetExportMetadata(member.GetCustomAttributes());
                    var contract = new CompositionContract(exportAttribute.ContractName, exportAttribute.ContractType ?? member.PropertyType);
                    var exportDefinition = new ExportDefinition(contract, exportMetadataOnMember);
                    exportsOnMembers.Add(member, exportDefinition);
                }
            }

            MethodInfo onImportsSatisfied = null;
            foreach (var method in partType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (method.GetCustomAttribute<OnImportsSatisfiedAttribute>() != null)
                {
                    Verify.Operation(method.GetParameters().Length == 0, "OnImportsSatisfied method should take no parameters.");
                    Verify.Operation(onImportsSatisfied == null, "Only one OnImportsSatisfied method is supported.");
                    onImportsSatisfied = method;
                }
            }

            if (exportsOnMembers.Count > 0 || exportsOnType.Count > 0)
            {
                var importingConstructorParameters = ImmutableList.CreateBuilder<ImportDefinition>();
                var importingCtor = GetImportingConstructor(partType, typeof(ImportingConstructorAttribute), publicOnly: true);
                foreach (var parameter in importingCtor.GetParameters())
                {
                    var importDefinition = CreateImportDefinition(
                            parameter.ParameterType,
                            parameter.GetCustomAttributes(),
                            GetImportConstraints(parameter.GetCustomAttributes()));
                    if (importDefinition.Cardinality == ImportCardinality.ZeroOrMore)
                    {
                        Verify.Operation(PartDiscovery.IsImportManyCollectionTypeCreateable(importDefinition), "Collection must be public with a public constructor when used with an [ImportingConstructor].");
                    }

                    importingConstructorParameters.Add(importDefinition);
                }

                return new ComposablePartDefinition(partType, exportsOnType.ToImmutable(), exportsOnMembers.ToImmutable(), imports.ToImmutable(), sharingBoundary, onImportsSatisfied, importingConstructorParameters.ToImmutable());
            }
            else
            {
                return null;
            }
        }

        private static IReadOnlyDictionary<string, object> GetExportMetadata(IEnumerable<Attribute> attributes)
        {
            Requires.NotNull(attributes, "attributes");

            var result = ImmutableDictionary.CreateBuilder<string, object>();
            foreach (var attribute in attributes)
            {
                var exportMetadataAttribute = attribute as ExportMetadataAttribute;
                if (exportMetadataAttribute != null)
                {
                    result.Add(exportMetadataAttribute.Name, exportMetadataAttribute.Value);
                }
                else if (attribute.GetType().GetCustomAttribute<MetadataAttributeAttribute>() != null)
                {
                    var properties = attribute.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
                    foreach (var property in properties.Where(p => p.DeclaringType != typeof(Attribute)))
                    {
                        result.Add(property.Name, property.GetValue(attribute));
                    }
                }
            }

            return result.ToImmutable();
        }

        private static bool TryCreateImportDefinition(Type propertyOrFieldType, IEnumerable<Attribute> attributes, IReadOnlyCollection<IImportSatisfiabilityConstraint> importConstraints, out ImportDefinition importDefinition)
        {
            Requires.NotNull(propertyOrFieldType, "propertyOrFieldType");

            var importAttribute = attributes.OfType<ImportAttribute>().SingleOrDefault();
            var importManyAttribute = attributes.OfType<ImportManyAttribute>().SingleOrDefault();
            var sharingBoundaryAttribute = attributes.OfType<SharingBoundaryAttribute>().SingleOrDefault();

            var sharingBoundaries = ImmutableHashSet.Create<string>();
            if (sharingBoundaryAttribute != null)
            {
                Verify.Operation(propertyOrFieldType.IsExportFactoryTypeV2(), "{0} is expected only on imports of ExportFactory<T>", typeof(SharingBoundaryAttribute).Name);
                sharingBoundaries = sharingBoundaries.Union(sharingBoundaryAttribute.SharingBoundaryNames);
            }

            if (importAttribute != null)
            {
                Type contractType = propertyOrFieldType;
                if (contractType.IsAnyLazyType() || contractType.IsExportFactoryTypeV2())
                {
                    contractType = contractType.GetGenericArguments()[0];
                }

                var contract = new CompositionContract(importAttribute.ContractName, contractType);
                importDefinition = new ImportDefinition(
                    contract,
                    importAttribute.AllowDefault ? ImportCardinality.OneOrZero : ImportCardinality.ExactlyOne,
                    propertyOrFieldType,
                    importConstraints,
                    sharingBoundaries);
                return true;
            }
            else if (importManyAttribute != null)
            {
                Type contractType = GetElementFromImportingMemberType(propertyOrFieldType, importMany: true);
                var contract = new CompositionContract(importManyAttribute.ContractName, contractType);
                importDefinition = new ImportDefinition(
                   contract,
                   ImportCardinality.ZeroOrMore,
                   propertyOrFieldType,
                   importConstraints,
                   sharingBoundaries);
                return true;
            }
            else
            {
                importDefinition = null;
                return false;
            }
        }

        private static ImportDefinition CreateImportDefinition(Type propertyOrFieldType, IEnumerable<Attribute> attributes, IReadOnlyCollection<IImportSatisfiabilityConstraint> importConstraints)
        {
            ImportDefinition result;
            if (!TryCreateImportDefinition(propertyOrFieldType, attributes, importConstraints, out result))
            {
                Assumes.True(TryCreateImportDefinition(propertyOrFieldType, attributes.Concat(new Attribute[] { new ImportAttribute() }), importConstraints, out result));
            }

            return result;
        }

        private static IReadOnlyCollection<IImportSatisfiabilityConstraint> GetImportConstraints(IEnumerable<Attribute> attributes)
        {
            Requires.NotNull(attributes, "attributes");

            return (from importConstraint in attributes.OfType<ImportMetadataConstraintAttribute>()
                    select new ImportMetadataValueConstraint(importConstraint.Name, importConstraint.Value)).ToImmutableList();
        }

        public override IReadOnlyCollection<ComposablePartDefinition> CreateParts(Assembly assembly)
        {
            Requires.NotNull(assembly, "assembly");

            var parts = from type in assembly.GetExportedTypes()
                        where type.GetCustomAttribute<PartNotDiscoverableAttribute>() == null
                        let part = this.CreatePart(type)
                        where part != null
                        select part;
            return parts.ToImmutableArray();
        }
    }
}
