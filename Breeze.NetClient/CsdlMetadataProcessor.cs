﻿using Breeze.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Breeze.NetClient {

  class CsdlMetadataProcessor {

    public CsdlMetadataProcessor(MetadataStore metadataStore, String jsonMetadata) {
      _metadataStore = metadataStore;
      var json = (JObject)JsonConvert.DeserializeObject(jsonMetadata);
      _schema = json["schema"];
      _namespace = (String)_schema["namespace"];

      var mapping = (String)_schema["cSpaceOSpaceMapping"];
      if (mapping != null) {
        var tmp = (JArray)JsonConvert.DeserializeObject(mapping);
        _cSpaceOSpaceMap = tmp.ToDictionary(v => (String)v[0], v => (String)v[1]);
      }

      var entityTypes = ToEnumerable(_schema["entityType"]).Cast<JObject>()
        .Select(ParseCsdlEntityType).ToList();
      var complexTypes = ToEnumerable(_schema["complexType"]).Cast<JObject>()
        .Select(ParseCsdlComplexType).ToList();
      entityTypes.ForEach(et => ResolveComplexTypeRefs(et));

      var entityContainer = _schema["entityContainer"];
      if (entityContainer != null) {
        var entitySets = ToEnumerable(entityContainer["entitySet"]).Cast<JObject>().ToList();
        entitySets.ForEach(es => {
          var entityTypeInfo = ParseTypeName((String) es["entityType"]);
          var entityType = _metadataStore.GetEntityType(entityTypeInfo.TypeName);
          var resourceName = (String) es["name"];
          _metadataStore.AddResourceName(resourceName, entityType, true);
        });
      }
    }

    private void ResolveComplexTypeRefs(EntityType et) {
      et.ComplexProperties.Where(cp => cp.ComplexType == null)
        .ForEach(cp => cp.ComplexType = _metadataStore.GetComplexType(cp.ComplexTypeName));
    }

    private EntityType ParseCsdlEntityType(JObject csdlEntityType) {
      var abstractVal = (String)csdlEntityType["abstract"];
      var baseTypeVal = (String)csdlEntityType["baseType"];
      var nameVal = (String)csdlEntityType["name"];

      var isAbstract = abstractVal == "true";
      var entityType = new EntityType {
        ShortName = nameVal,
        Namespace = GetNamespaceFor(nameVal),
      };
      if (baseTypeVal != null) {
        var baseTypeInfo = ParseTypeName(baseTypeVal);
        var baseTypeName = baseTypeInfo.TypeName;
        entityType.BaseTypeName = baseTypeName;
        var baseEntityType = _metadataStore.GetEntityType(baseTypeName, true);
        if (baseEntityType == null) {
          CompleteParseCsdlEntityType(entityType, csdlEntityType, baseEntityType);
        } else {
          List<DeferredTypeInfo> deferrals;
          if (_deferredTypeMap.ContainsKey(baseTypeName)) {
            deferrals = _deferredTypeMap[baseTypeName];
          } else {
            deferrals = new List<DeferredTypeInfo>();
            _deferredTypeMap[baseTypeName] = deferrals;
          }
          deferrals.Add(new DeferredTypeInfo { EntityType = entityType, CsdlEntityType = csdlEntityType });
        }
      } else {
        CompleteParseCsdlEntityType(entityType, csdlEntityType, null);
      }
      // entityType may or may not have been added to the metadataStore at this point.
      return entityType;
    }

    private void CompleteParseCsdlEntityType(EntityType entityType, JObject csdlEntityType, EntityType baseEntityType) {
      var baseKeyNamesOnServer = new List<string>();
      if (baseEntityType != null) {
        entityType.BaseEntityType = baseEntityType;
        entityType.AutoGeneratedKeyType = baseEntityType.AutoGeneratedKeyType;
        baseKeyNamesOnServer = baseEntityType.KeyProperties.Select(dp => dp.NameOnServer).ToList();
        baseEntityType.DataProperties.ForEach(dp => {
          var newDp = new DataProperty(dp);
          newDp.IsInherited = true;
          entityType.AddDataProperty(dp);
        });
        baseEntityType.NavigationProperties.ForEach(np => {
          var newNp = new NavigationProperty(np);
          newNp.IsInherited = true;
          entityType.AddNavigationProperty(np);
        });
      }
      var keyVal = csdlEntityType["key"];
      var keyNamesOnServer = keyVal == null
        ? new List<String>()
        : ToEnumerable(keyVal["propertyRef"]).Select(x => (String)x["name"]).ToList();
      keyNamesOnServer.AddRange(baseKeyNamesOnServer);

      ToEnumerable(csdlEntityType["property"]).ForEach(csdlDataProp => {
        ParseCsdlDataProperty(entityType, (JObject)csdlDataProp, keyNamesOnServer);
      });

      ToEnumerable(csdlEntityType["navigationProperty"]).ForEach(csdlNavProp => {
        ParseCsdlNavigationProperty(entityType, (JObject)csdlNavProp);
      });

      _metadataStore.AddEntityType(entityType);
      

      List<DeferredTypeInfo> deferrals;
      if (_deferredTypeMap.TryGetValue(entityType.Name, out deferrals)) {
        deferrals.ForEach( dti => {
          CompleteParseCsdlEntityType(dti.EntityType, dti.CsdlEntityType, entityType);
        });
        _deferredTypeMap.Remove(entityType.Name);
      }
    
    }

    private DataProperty ParseCsdlDataProperty(StructuralType parentType, JObject csdlProperty, List<String> keyNamesOnServer) {
      DataProperty dp;
      var typeParts = ExtractTypeNameParts(csdlProperty);

      if (typeParts.Length == 2) {
        dp = ParseCsdlSimpleProperty(parentType, csdlProperty, keyNamesOnServer);
      } else {
        if (IsEnumType(csdlProperty)) {
          dp = ParseCsdlSimpleProperty(parentType, csdlProperty, keyNamesOnServer);
          dp.EnumTypeName = (String)csdlProperty["type"];
        } else {
          dp = ParseCsdlComplexProperty(parentType, csdlProperty);
        }
      }

      if (dp != null) {
        parentType.AddDataProperty(dp);
        AddValidators(dp);
      }
      return dp;
    }

    private DataProperty ParseCsdlSimpleProperty(StructuralType parentType, JObject csdlProperty, List<String> keyNamesOnServer) {

      var typeVal = (String)csdlProperty["type"];
      var nameVal = (String)csdlProperty["name"];
      var nullableVal = (String)csdlProperty["nullable"];
      var maxLengthVal = (String)csdlProperty["maxLength"];
      var concurrencyModeVal = (String)csdlProperty["concurrencyMode"];

      var dataType = DataType.FromEdmType(typeVal);
      if (dataType == DataType.Undefined) {
        parentType.Warnings.Add("Unable to recognize DataType for property: " + nameVal + " DateType: " + typeVal);
      }

      var isNullable = nullableVal == "true" || nullableVal == null;
      var entityType = parentType as EntityType;
      bool isPartOfKey = false;
      bool isAutoIncrementing = false;
      if (entityType != null) {
        isPartOfKey = keyNamesOnServer != null && keyNamesOnServer.IndexOf(nameVal) >= 0;
        if (isPartOfKey && entityType.AutoGeneratedKeyType == AutoGeneratedKeyType.None) {
          if (IsIdentityProperty(csdlProperty)) {
            isAutoIncrementing = true;
            entityType.AutoGeneratedKeyType = AutoGeneratedKeyType.Identity;
          }
        }
      }

      var defaultValue = csdlProperty["defaultValue"] ?? (isNullable ? null : dataType.DefaultValue);
 

      // TODO: nit - don't set maxLength if null;
      var maxLength = (maxLengthVal == null || maxLengthVal == "Max") ? (Int64?)null : Int64.Parse(maxLengthVal);
      var concurrencyMode = concurrencyModeVal == "fixed" ? ConcurrencyMode.Fixed : ConcurrencyMode.None;
      var dp = new DataProperty() {
        ParentType = parentType,
        NameOnServer = nameVal,
        DataType = dataType,
        IsNullable = isNullable,
        IsPartOfKey = isPartOfKey,
        MaxLength = maxLength,
        DefaultValue = defaultValue,
        // fixedLength: fixedLength,
        ConcurrencyMode = concurrencyMode,
        IsScalar = true ,
        IsAutoIncrementing = isAutoIncrementing
      };

      if (dataType == DataType.Undefined) {
        dp.RawTypeName = typeVal;
      }
      return dp;
    }

    private DataProperty ParseCsdlComplexProperty(StructuralType parentType, JObject csdlProperty) {
      // Complex properties are never nullable ( per EF specs)
      // var isNullable = csdlProperty.nullable === 'true' || csdlProperty.nullable == null;

      var complexTypeName = ParseTypeName((String)csdlProperty["type"]).TypeName;
      // can't set the name until we go thru namingConventions and these need the dp.
      var dp = new DataProperty() {
        ParentType = parentType,
        NameOnServer = (String)csdlProperty["name"],
        ComplexTypeName = complexTypeName,
        IsNullable = false,
        IsScalar = true,
        ConcurrencyMode = ConcurrencyMode.None,
        
      };

      return dp;
    }

    private NavigationProperty ParseCsdlNavigationProperty(EntityType parentType, JObject csdlProperty) {
      var association = GetAssociation(csdlProperty);
      var toRoleVal = (String)csdlProperty["toRole"];
      var fromRoleVal = (String)csdlProperty["fromRole"];
      var nameVal = (String)csdlProperty["name"];
      var toEnd = ToEnumerable(association["end"]).FirstOrDefault(end => (String)end["role"] == toRoleVal);
      var isScalar = (String)toEnd["multiplicity"] != "*";
      var dataType = ParseTypeName((String)toEnd["type"]).TypeName;
      var constraintVal = association["referentialConstraint"];
      if (constraintVal == null) {
        return null;
        // TODO: Revisit this later - right now we just ignore many-many and assocs with missing constraints.
        //
        // if (association.end[0].multiplicity == "*" && association.end[1].multiplicity == "*") {
        //    // many to many relation
        //    ???
        // } else {
        //    throw new Error("Foreign Key Associations must be turned on for this model");
        // }
      }
      var np = new NavigationProperty() {
        ParentType = parentType,
        NameOnServer = nameVal,
        EntityTypeName = dataType,
        IsScalar = isScalar,
        AssociationName = (String)association["name"]
        
      };


      var principal = constraintVal["principal"];
      var dependent = constraintVal["dependent"];

      var propRefs = ToEnumerable(dependent["propertyRef"]);
      var fkNames = propRefs.Select(pr => (String)pr["name"]).ToSafeList();
      if (fromRoleVal == (String)principal["role"]) {
        np._invForeignKeyNamesOnServer = fkNames;
      } else {
        np._foreignKeyNamesOnServer = fkNames;
      }

      parentType.AddProperty(np);
      return np;

    }

    private JObject GetAssociation(JObject csdlNavProperty) {
      var assocsVal = _schema["association"];
      if (assocsVal == null) return null;

      var relationshipVal = (String)csdlNavProperty["relationship"];
      var assocName = ParseTypeName(relationshipVal).ShortTypeName;

      var association = ToEnumerable(assocsVal).FirstOrDefault(assoc => (String)assoc["name"] == assocName);

      return (JObject)association;
    }

    private ComplexType ParseCsdlComplexType(JObject csdlComplexType) {
      var nameVal = (String)csdlComplexType["name"];
      var ns = GetNamespaceFor(nameVal);
      var complexType = new ComplexType {
        ShortName = nameVal,
        Namespace = ns,
      };

      ToEnumerable(csdlComplexType["property"])
        .ForEach(prop => ParseCsdlDataProperty(complexType, (JObject)prop, null));

      _metadataStore.AddComplexType(complexType);
      return complexType;
    }

    private TypeNameInfo ParseTypeName(String clrTypeName) {
      if (String.IsNullOrEmpty(clrTypeName)) return null;
      if (clrTypeName.StartsWith(MetadataStore.ANONTYPE_PREFIX)) {
        return new TypeNameInfo() {
          ShortTypeName = clrTypeName,
          Namespace = "",
          TypeName = clrTypeName,
          IsAnonymous = true,
        };
      }

      var entityTypeNameNoAssembly = clrTypeName.Split(',')[0];
      var nameParts = entityTypeNameNoAssembly.Split('.');
      if (nameParts.Length > 1) {
        var shortName = nameParts[nameParts.Length - 1];
        // var nsParts = nameParts.Take(nameParts.Length - 1).ToArray();
        var ns = GetNamespaceFor(shortName);

        return new TypeNameInfo() {
          ShortTypeName = shortName,
          Namespace = ns,
          TypeName = StructuralType.QualifyTypeName(shortName, ns)
        };
      } else {
        return new TypeNameInfo() {
          ShortTypeName = clrTypeName,
          Namespace = "",
          TypeName = clrTypeName
        };
      }
    }

    private String GetNamespaceFor(String shortName) {

      if (_cSpaceOSpaceMap != null) {
        var cSpaceName = _namespace + "." + shortName;
        String oSpaceName;
        if (_cSpaceOSpaceMap.TryGetValue(cSpaceName, out oSpaceName)) {
          var ns = oSpaceName.Substring(0, oSpaceName.Length - (shortName.Length + 1));
          return ns;
        }
      }
      return _namespace;
    }

    private bool IsIdentityProperty(JObject csdlProperty) {

      var subProp = csdlProperty.Properties().FirstOrDefault(p => p.Name.IndexOf("StoreGeneratedPattern") > 0);
      if (subProp != null) {
        return subProp.Value.ToObject<String>() == "Identity";
      } else {
        // see if Odata feed
        var extensionsVal = csdlProperty["extensions"];
        if (extensionsVal == null) return false;
        // TODO: NOT YET TESTED
        var identityExtn = ToEnumerable(extensionsVal).FirstOrDefault(extn => {
          return (String)extn["name"] == "StoreGeneratedPattern" && (String)extn["value"] == "Identity";
        });
        return identityExtn != null;
      }
    }

    private void AddValidators(DataProperty dp) {
      if (!dp.IsNullable) {
        dp._validators.Add(new RequiredValidator().Intern());
      }
      if (dp.MaxLength.HasValue) {
        var vr = new MaxLengthValidator( (Int32) dp.MaxLength.Value).Intern();
        dp._validators.Add(vr);
      }
    }

    // function addValidators(dataProperty) {
    //    var typeValidator;
    //    if (!dataProperty.isNullable) {
    //        dataProperty.validators.push(Validator.required());
    //    }

    //    if (dataProperty.isComplexProperty) return;

    //    if (dataProperty.dataType === DataType.String) {
    //        if (dataProperty.maxLength) {
    //            var validatorArgs = { maxLength: dataProperty.maxLength };
    //            typeValidator = Validator.maxLength(validatorArgs);
    //        } else {
    //            typeValidator = Validator.string();
    //        }
    //    } else {
    //        typeValidator = dataProperty.dataType.validatorCtor();
    //    }

    //    dataProperty.validators.push(typeValidator);

    //}

    private bool IsEnumType(JObject csdlProperty) {
      var enumTypeVal = _schema["enumType"];
      if (enumTypeVal == null) return false;
      var enumTypes = ToEnumerable(enumTypeVal);
      var typeParts = ExtractTypeNameParts(csdlProperty);
      var baseTypeName = typeParts[typeParts.Length - 1];
      return enumTypes.Any(enumType => ((String)enumType["name"] == baseTypeName));
    }

    private String[] ExtractTypeNameParts(JObject csdlProperty) {
      var typeParts = ((String)csdlProperty["type"]).Split('.');
      return typeParts;
    }

    private IEnumerable<T> ToEnumerable<T>(T d) {
      if (d == null) {
        return Enumerable.Empty<T>();
      } else if (d.GetType() == typeof(JArray)) {
        return ((IEnumerable)d).Cast<T>();
      } else {
        return new T[] { d };
      }
    }

    internal class TypeNameInfo {
      public String ShortTypeName { get; set; }
      public String Namespace { get; set; }
      public String TypeName { get; set; }
      public Boolean IsAnonymous { get; set; }
    }

    private class DeferredTypeInfo {
      public EntityType EntityType { get; set; }
      public JObject CsdlEntityType { get; set; }
    }

    private JToken _schema;
    private String _namespace;
    private MetadataStore _metadataStore;
    private Dictionary<String, String> _cSpaceOSpaceMap;
    private Dictionary<String, List<DeferredTypeInfo>> _deferredTypeMap = new Dictionary<String, List<DeferredTypeInfo>>();



  }
}
