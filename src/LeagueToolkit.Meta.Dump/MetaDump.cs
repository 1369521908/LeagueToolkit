﻿using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Primitives;
using LeagueToolkit.Hashing;
using LeagueToolkit.Meta.Attributes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace LeagueToolkit.Meta.Dump
{
    public sealed class MetaDump
    {
        public string Version { get; set; }
        public Dictionary<string, MetaDumpClass> Classes { get; set; }

        /// <summary>
        /// The name of the namespace declaration which contains the meta class declarations
        /// </summary>
        private const string META_CLASSES_NAMESPACE = $"{nameof(LeagueToolkit)}.{nameof(LeagueToolkit.Meta)}.Classes";

        /* -------------------------------- PUBLIC DUMPING API -------------------------------- */
        #region Public Dumping API
        public void WriteMetaClasses(string file, IEnumerable<string> classes, IEnumerable<string> properties) =>
            WriteMetaClasses(File.Create(file), classes, properties);

        public void WriteMetaClasses(Stream stream, IEnumerable<string> classes, IEnumerable<string> properties)
        {
            // Create dictionaries from collections
            Dictionary<uint, string> classesMap = new();
            Dictionary<uint, string> propertiesMap = new();

            foreach (string className in classes)
            {
                classesMap.Add(Fnv1a.HashLower(className), className);
            }

            foreach (string propertyName in properties)
            {
                propertiesMap.Add(Fnv1a.HashLower(propertyName), propertyName);
            }

            WriteMetaClasses(stream, classesMap, propertiesMap);
        }

        public void WriteMetaClasses(
            string file,
            IReadOnlyDictionary<uint, string> classes,
            IReadOnlyDictionary<uint, string> properties
        ) => WriteMetaClasses(File.Create(file), classes, properties);

        public void WriteMetaClasses(
            Stream stream,
            IReadOnlyDictionary<uint, string> classes,
            IReadOnlyDictionary<uint, string> properties
        )
        {
            CompilationUnitSyntax compilationUnit = CompilationUnit();

            // Create compilation unit
            compilationUnit = compilationUnit
                .WithUsings(new(TakeRequiredUsingDirectives(GetRequiredTypes())))
                .WithMembers(
                    SingletonList<MemberDeclarationSyntax>(
                        NamespaceDeclaration(ParseName(META_CLASSES_NAMESPACE, consumeFullText: true))
                            .WithMembers(new(TakeMetaClassDeclarations(classes, properties)))
                    )
                );

            //using AdhocWorkspace workspace = new();
            //SyntaxNode metaSyntax = Formatter.Format(compilationUnit, workspace);

            using StreamWriter syntaxWriter = new(stream);
            compilationUnit.NormalizeWhitespace(eol: "\n", elasticTrivia: true).WriteTo(syntaxWriter);
        }
        #endregion
        /* -------------------------------- PUBLIC DUMPING API -------------------------------- */

        private IEnumerable<TypeDeclarationSyntax> TakeMetaClassDeclarations(
            IReadOnlyDictionary<uint, string> classes,
            IReadOnlyDictionary<uint, string> properties
        )
        {
            foreach (var (classHash, @class) in this.Classes.Select(x => (x.Key, x.Value)))
            {
                yield return CreateMetaClassDeclaration(classHash, @class, classes, properties);
            }
        }

        private TypeDeclarationSyntax CreateMetaClassDeclaration(
            string classHash,
            MetaDumpClass @class,
            IReadOnlyDictionary<uint, string> classes,
            IReadOnlyDictionary<uint, string> properties
        )
        {
            List<SyntaxToken> modifiers = new() { Token(SyntaxKind.PublicKeyword) };

            TypeDeclarationSyntax declarationSyntax = @class.Is.SecondaryBase switch
            {
                true => InterfaceDeclaration(GetClassNameOrDefault(classHash, classes)),
                false => ClassDeclaration(GetClassNameOrDefault(classHash, classes))
            };

            if (@class.Is.SecondaryBase is false && @class.Is.Interface)
                modifiers.Add(Token(SyntaxKind.AbstractKeyword));

            return declarationSyntax
                // Add attributes
                .WithAttributeLists(SingletonList(CreateMetaClassAttributeList(classHash, classes)))
                // Add modifiers
                .WithModifiers(TokenList(modifiers))
                // Add bases
                .WithBaseList(CreateMetaClassBaseList(@class, classes))
                // Add members
                .WithMembers(
                    List<MemberDeclarationSyntax>(
                        TakeMetaClassPropertyDeclarations(classHash, @class, classes, properties)
                    )
                );
        }

        private AttributeListSyntax CreateMetaClassAttributeList(
            string classHash,
            IReadOnlyDictionary<uint, string> classes
        )
        {
            bool hasAttributeClassName = classes.TryGetValue(
                Convert.ToUInt32(classHash, 16),
                out string attributeClassName
            );

            return AttributeList(
                SingletonSeparatedList(
                    Attribute(IdentifierName(nameof(MetaClassAttribute)))
                        .WithArgumentList(
                            AttributeArgumentList(
                                SingletonSeparatedList(
                                    AttributeArgument(
                                        LiteralExpression(
                                            hasAttributeClassName
                                                ? SyntaxKind.StringLiteralExpression
                                                : SyntaxKind.NumericLiteralExpression,
                                            hasAttributeClassName
                                                ? Literal(attributeClassName)
                                                : Literal(Convert.ToUInt32(classHash, 16))
                                        )
                                    )
                                )
                            )
                        )
                )
            );
        }

        private BaseListSyntax CreateMetaClassBaseList(MetaDumpClass @class, IReadOnlyDictionary<uint, string> classes)
        {
            IEnumerable<SimpleBaseTypeSyntax> bases = @class
                .TakeSecondaryBases(this.Classes, false)
                // Special case - Class has defined both Base and Secondary Base - write only primary base (ex: StaticMaterialDef)
                .SkipWhile(x => @class.SecondaryBases.Count > 0 && !string.IsNullOrEmpty(@class.Base))
                .Select(
                    secondaryBaseHash =>
                        SimpleBaseType(IdentifierName(GetClassNameOrDefault(secondaryBaseHash, classes)))
                );

            SimpleBaseTypeSyntax @base = SimpleBaseType(
                IdentifierName(
                    string.IsNullOrEmpty(@class.Base) ? nameof(IMetaClass) : GetClassNameOrDefault(@class.Base, classes)
                )
            );

            // If there is no primary base and no secondary bases, the base is IMetaClass
            // If there are no secondary bases, we add only the primary base
            // (there cannot be 2 base classes, see comment above)
            if (bases.Count() == 0)
            {
                bases = bases.Prepend(@base);
            }

            return BaseList(SeparatedList<BaseTypeSyntax>(bases));
        }

        private static IEnumerable<PropertyDeclarationSyntax> TakeMetaClassPropertyDeclarations(
            string classHash,
            MetaDumpClass @class,
            IReadOnlyDictionary<uint, string> classes,
            IReadOnlyDictionary<uint, string> properties
        )
        {
            // Create property declarations
            foreach (var (hash, property) in @class.Properties.Select(x => (x.Key, x.Value)))
            {
                yield return CreateMetaClassPropertyDeclaration(classHash, @class, hash, property, classes, properties);
            }
        }

        private static PropertyDeclarationSyntax CreateMetaClassPropertyDeclaration(
            string classHash,
            MetaDumpClass @class,
            string propertyHash,
            MetaDumpProperty property,
            IReadOnlyDictionary<uint, string> classes,
            IReadOnlyDictionary<uint, string> properties
        )
        {
            TypeSyntax typeSyntax = CreatePropertyTypeDeclaration(property, classes);
            string name = StylizePropertyName(GetPropertyNameOrDefault(propertyHash, properties));

            // Check that property name isn't the same as the class name
            string className = GetClassNameOrDefault(classHash, classes);
            if (className == name)
            {
                name = $"m{name}";
            }

            PropertyDeclarationSyntax propertyDeclaration = PropertyDeclaration(typeSyntax, name)
                // Add attribute
                .WithAttributeLists(CreatePropertyAttributesSyntax(propertyHash, property, classes, properties))
                // Add visibility
                .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)))
                // Add accessor
                .WithAccessorList(
                    AccessorList(
                        List(
                            new AccessorDeclarationSyntax[]
                            {
                                AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                                    .WithSemicolonToken(Token(SyntaxKind.SemicolonToken)),
                                AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
                                    .WithSemicolonToken(Token(SyntaxKind.SemicolonToken))
                            }
                        )
                    )
                );

            if (@class.Defaults is not null)
            {
                propertyDeclaration = propertyDeclaration
                    .WithInitializer(CreatePropertyInitializer(@class.Defaults[propertyHash], property))
                    .WithSemicolonToken(Token(SyntaxKind.SemicolonToken));
            }

            return propertyDeclaration;
        }

        /* ---------------------------- PROPERTY ATTRIBUTE CREATION ----------------------------- */
        #region Property Attribute Creation
        private static SyntaxList<AttributeListSyntax> CreatePropertyAttributesSyntax(
            string propertyHash,
            MetaDumpProperty property,
            IReadOnlyDictionary<uint, string> classes,
            IReadOnlyDictionary<uint, string> properties
        ) =>
            SingletonList(
                AttributeList(
                    SingletonSeparatedList(
                        Attribute(
                            IdentifierName(nameof(MetaPropertyAttribute)),
                            AttributeArgumentList(
                                SeparatedList(
                                    new AttributeArgumentSyntax[]
                                    {
                                        CreatePropertyNameAttributeArgument(propertyHash, properties),
                                        CreatePropertyTypeAttributeArgument(property),
                                        CreatePropertyOtherClassAttributeArgument(property, classes),
                                        CreatePropertyPrimaryTypeAttributeArgument(property),
                                        CreatePropertySecondaryTypeAttributeArgument(property)
                                    }
                                )
                            )
                        )
                    )
                )
            );

        private static AttributeArgumentSyntax CreatePropertyNameAttributeArgument(
            string propertyHash,
            IReadOnlyDictionary<uint, string> properties
        )
        {
            bool hasName = properties.TryGetValue(Convert.ToUInt32(propertyHash, 16), out string name);

            return AttributeArgument(
                LiteralExpression(
                    hasName ? SyntaxKind.StringLiteralExpression : SyntaxKind.NumericLiteralExpression,
                    hasName ? Literal(name) : Literal(Convert.ToUInt32(propertyHash, 16))
                )
            );
        }

        private static AttributeArgumentSyntax CreatePropertyTypeAttributeArgument(MetaDumpProperty property) =>
            AttributeArgument(
                MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    IdentifierName(nameof(BinPropertyType)),
                    IdentifierName(Enum.GetName(typeof(BinPropertyType), property.Type))
                )
            );

        private static AttributeArgumentSyntax CreatePropertyOtherClassAttributeArgument(
            MetaDumpProperty property,
            IReadOnlyDictionary<uint, string> classes
        )
        {
            string otherClass = property.OtherClass is null ? "" : GetClassNameOrDefault(property.OtherClass, classes);

            return AttributeArgument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(otherClass)));
        }

        private static AttributeArgumentSyntax CreatePropertyPrimaryTypeAttributeArgument(MetaDumpProperty property)
        {
            BinPropertyType secondaryType = property switch
            {
                MetaDumpProperty { Map: not null } notNullMapProperty => notNullMapProperty.Map.KeyType,
                MetaDumpProperty { Container: not null } notNullContainerProperty
                    => notNullContainerProperty.Container.Type,
                _ => BinPropertyType.None
            };

            return AttributeArgument(
                MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    IdentifierName(nameof(BinPropertyType)),
                    IdentifierName(Enum.GetName(typeof(BinPropertyType), secondaryType))
                )
            );
        }

        private static AttributeArgumentSyntax CreatePropertySecondaryTypeAttributeArgument(MetaDumpProperty property)
        {
            BinPropertyType primaryType = property switch
            {
                MetaDumpProperty { Map: not null } notNullMapProperty => notNullMapProperty.Map.ValueType,
                _ => BinPropertyType.None
            };

            return AttributeArgument(
                MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    IdentifierName(nameof(BinPropertyType)),
                    IdentifierName(Enum.GetName(typeof(BinPropertyType), primaryType))
                )
            );
        }

        #endregion
        /* ---------------------------- PROPERTY ATTRIBUTE CREATION ----------------------------- */

        /* ------------------------- PROPERTY TYPE DECLARATION CREATORS ------------------------- */
        #region Property Type Declaration Creators
        private static TypeSyntax CreatePropertyTypeDeclaration(
            MetaDumpProperty property,
            IReadOnlyDictionary<uint, string> classes
        ) =>
            property.Type switch
            {
                BinPropertyType.Container
                    => CreateContainerTypeDeclaration(property.OtherClass, property.Container, false, classes),
                BinPropertyType.UnorderedContainer
                    => CreateContainerTypeDeclaration(property.OtherClass, property.Container, true, classes),
                BinPropertyType.Struct => CreateStructureTypeDeclaration(property.OtherClass, classes),
                BinPropertyType.Embedded => CreateEmbeddedTypeDeclaration(property.OtherClass, classes),
                BinPropertyType.Optional
                    => CreateOptionalTypeDeclaration(property.OtherClass, property.Container, classes),
                BinPropertyType.Map => CreateMapTypeDeclaration(property.OtherClass, property.Map, classes),
                BinPropertyType type => CreatePrimitivePropertyTypeDeclaration(type, false)
            };

        private static TypeSyntax CreatePrimitivePropertyTypeDeclaration(BinPropertyType type, bool nullable)
        {
            TypeSyntax typeDeclaration = type switch
            {
                BinPropertyType.Bool => PredefinedType(Token(SyntaxKind.BoolKeyword)),
                BinPropertyType.I8 => PredefinedType(Token(SyntaxKind.SByteKeyword)),
                BinPropertyType.U8 => PredefinedType(Token(SyntaxKind.ByteKeyword)),
                BinPropertyType.I16 => PredefinedType(Token(SyntaxKind.ShortKeyword)),
                BinPropertyType.U16 => PredefinedType(Token(SyntaxKind.UShortKeyword)),
                BinPropertyType.I32 => PredefinedType(Token(SyntaxKind.IntKeyword)),
                BinPropertyType.U32 => PredefinedType(Token(SyntaxKind.UIntKeyword)),
                BinPropertyType.I64 => PredefinedType(Token(SyntaxKind.LongKeyword)),
                BinPropertyType.U64 => PredefinedType(Token(SyntaxKind.ULongKeyword)),
                BinPropertyType.F32 => PredefinedType(Token(SyntaxKind.FloatKeyword)),
                BinPropertyType.Vector2 => ParseTypeName(nameof(Vector2)),
                BinPropertyType.Vector3 => ParseTypeName(nameof(Vector3)),
                BinPropertyType.Vector4 => ParseTypeName(nameof(Vector4)),
                BinPropertyType.Matrix44 => ParseTypeName(nameof(Matrix4x4)),
                BinPropertyType.Color => ParseTypeName(nameof(Color)),
                BinPropertyType.String => PredefinedType(Token(SyntaxKind.StringKeyword)),
                BinPropertyType.Hash => ParseTypeName(nameof(MetaHash)),
                BinPropertyType.WadChunkLink => ParseTypeName(nameof(MetaWadEntryLink)),
                BinPropertyType.ObjectLink => ParseTypeName(nameof(MetaObjectLink)),
                BinPropertyType.BitBool => ParseTypeName(nameof(MetaBitBool)),
                BinPropertyType propertyType
                    => throw new InvalidOperationException("Invalid Primitive Property type: " + propertyType)
            };

            return nullable switch
            {
                true => NullableType(typeDeclaration),
                false => typeDeclaration
            };
        }

        private static GenericNameSyntax CreateContainerTypeDeclaration(
            string elementClass,
            MetaDumpContainer container,
            bool isUnorderedContainer,
            IReadOnlyDictionary<uint, string> classes
        )
        {
            TypeSyntax argumentTypeSyntax = container.Type switch
            {
                BinPropertyType.Struct => CreateStructureTypeDeclaration(elementClass, classes),
                BinPropertyType.Embedded => CreateEmbeddedTypeDeclaration(elementClass, classes),
                BinPropertyType type => CreatePrimitivePropertyTypeDeclaration(type, false)
            };

            SyntaxToken identifier = isUnorderedContainer
                ? Identifier(SanitizeNameOfGenericType(typeof(MetaUnorderedContainer<IMetaClass>)))
                : Identifier(SanitizeNameOfGenericType(typeof(MetaContainer<IMetaClass>)));
            return GenericName(identifier, TypeArgumentList(SingletonSeparatedList(argumentTypeSyntax)));
        }

        private static TypeSyntax CreateStructureTypeDeclaration(
            string classNameHash,
            IReadOnlyDictionary<uint, string> classes
        ) => ParseTypeName(GetClassNameOrDefault(classNameHash, classes));

        private static GenericNameSyntax CreateEmbeddedTypeDeclaration(
            string classNameHash,
            IReadOnlyDictionary<uint, string> classes
        )
        {
            string argumentTypeIdentifier = GetClassNameOrDefault(classNameHash, classes);
            return GenericName(
                Identifier(SanitizeNameOfGenericType(typeof(MetaEmbedded<IMetaClass>))),
                TypeArgumentList(SingletonSeparatedList(ParseTypeName(argumentTypeIdentifier, consumeFullText: true)))
            );
        }

        private static GenericNameSyntax CreateOptionalTypeDeclaration(
            string otherClass,
            MetaDumpContainer container,
            IReadOnlyDictionary<uint, string> classes
        )
        {
            TypeSyntax argumentTypeSyntax = container.Type switch
            {
                BinPropertyType.Struct => CreateStructureTypeDeclaration(otherClass, classes),
                BinPropertyType.Embedded => CreateEmbeddedTypeDeclaration(otherClass, classes),
                BinPropertyType type => CreatePrimitivePropertyTypeDeclaration(type, false)
            };

            return GenericName(
                Identifier(SanitizeNameOfGenericType(typeof(MetaOptional<object>))),
                TypeArgumentList(SingletonSeparatedList(argumentTypeSyntax))
            );
        }

        private static GenericNameSyntax CreateMapTypeDeclaration(
            string otherClass,
            MetaDumpMap map,
            IReadOnlyDictionary<uint, string> classes
        )
        {
            TypeSyntax keyDeclaration = CreatePrimitivePropertyTypeDeclaration(map.KeyType, false);
            TypeSyntax valueDeclaration = map.ValueType switch
            {
                BinPropertyType.Struct => CreateStructureTypeDeclaration(otherClass, classes),
                BinPropertyType.Embedded => CreateEmbeddedTypeDeclaration(otherClass, classes),
                BinPropertyType type => CreatePrimitivePropertyTypeDeclaration(type, false)
            };

            return GenericName(
                Identifier(SanitizeNameOfGenericType(typeof(Dictionary<object, object>))),
                TypeArgumentList(SeparatedList(new TypeSyntax[] { keyDeclaration, valueDeclaration }))
            );
        }
        #endregion
        /* ------------------------- PROPERTY TYPE DECLARATION CREATORS ------------------------- */

        /* ----------------------------------- NAME UTILITIES ----------------------------------- */
        #region Name Utilities
        private static string GetClassNameOrDefault(string hash, IReadOnlyDictionary<uint, string> classNames)
        {
            return classNames.GetValueOrDefault(Convert.ToUInt32(hash, 16), "Class" + hash);
        }

        private static string GetPropertyNameOrDefault(string hash, IReadOnlyDictionary<uint, string> propertyNames)
        {
            return propertyNames.GetValueOrDefault(Convert.ToUInt32(hash, 16), "m" + Convert.ToUInt32(hash, 16));
        }

        private static string StylizePropertyName(string propertyName)
        {
            if (propertyName[0] == 'm' && char.IsUpper(propertyName[1]))
            {
                return propertyName[1..];
            }
            else if (char.IsLower(propertyName[0]) && !char.IsNumber(propertyName[1]))
            {
                return char.ToUpper(propertyName[0]) + propertyName[1..];
            }
            else
            {
                return propertyName;
            }
        }
        #endregion
        /* ----------------------------------- NAME UTILITIES ----------------------------------- */

        /* -------------------------------- INITIALIZER CREATORS -------------------------------- */
        #region Initializer Creators

        private static EqualsValueClauseSyntax CreatePropertyInitializer(
            JsonElement defaultValue,
            MetaDumpProperty property
        )
        {
            ExpressionSyntax expression = defaultValue.ValueKind switch
            {
                // true
                JsonValueKind.True when (property.Type == BinPropertyType.Bool)
                    => LiteralExpression(SyntaxKind.TrueLiteralExpression),
                // false
                JsonValueKind.False when (property.Type == BinPropertyType.Bool)
                    => LiteralExpression(SyntaxKind.FalseLiteralExpression),
                // new(true)
                JsonValueKind.True when (property.Type == BinPropertyType.BitBool)
                    => ImplicitObjectCreationExpression(
                        ArgumentList(
                            SingletonSeparatedList(Argument(LiteralExpression(SyntaxKind.TrueLiteralExpression)))
                        ),
                        null
                    ),
                // new(false)
                JsonValueKind.False when (property.Type == BinPropertyType.BitBool)
                    => ImplicitObjectCreationExpression(
                        ArgumentList(
                            SingletonSeparatedList(Argument(LiteralExpression(SyntaxKind.FalseLiteralExpression)))
                        ),
                        null
                    ),
                // new(0U)
                JsonValueKind.String when (property.Type == BinPropertyType.Hash)
                    => ImplicitObjectCreationExpression(
                        ArgumentList(
                            SingletonSeparatedList(
                                Argument(
                                    LiteralExpression(
                                        SyntaxKind.NumericLiteralExpression,
                                        Literal(Convert.ToUInt32(defaultValue.GetString(), 16))
                                    )
                                )
                            )
                        ),
                        null
                    ),
                // new(0U)
                JsonValueKind.String when (property.Type == BinPropertyType.ObjectLink)
                    => ImplicitObjectCreationExpression(
                        ArgumentList(
                            SingletonSeparatedList(
                                Argument(
                                    LiteralExpression(
                                        SyntaxKind.NumericLiteralExpression,
                                        Literal(Convert.ToUInt32(defaultValue.GetString(), 16))
                                    )
                                )
                            )
                        ),
                        null
                    ),
                // new(0UL)
                JsonValueKind.String when (property.Type == BinPropertyType.WadChunkLink)
                    => ImplicitObjectCreationExpression(
                        ArgumentList(
                            SingletonSeparatedList(
                                Argument(
                                    LiteralExpression(
                                        SyntaxKind.NumericLiteralExpression,
                                        Literal(Convert.ToUInt64(defaultValue.GetString(), 16))
                                    )
                                )
                            )
                        ),
                        null
                    ),
                // new(new())
                JsonValueKind.Object when (property.Type == BinPropertyType.Embedded)
                    => ImplicitObjectCreationExpression(
                        ArgumentList(SingletonSeparatedList(Argument(ImplicitObjectCreationExpression()))),
                        null
                    ),
                // null
                JsonValueKind.Null when (property.Type == BinPropertyType.Struct)
                    => LiteralExpression(SyntaxKind.NullLiteralExpression),
                // new MetaOptional<T>(default(T), false)
                // new MetaOptional<T>(nullableValue, true)
                _ when (property.Type == BinPropertyType.Optional)
                    => CreateNullableInitializerSyntax(property, defaultValue),
                _ => CreateCommonInitializerSyntax(property.Type, defaultValue)
            };

            return EqualsValueClause(expression);
        }

        private static ExpressionSyntax CreateNullableInitializerSyntax(MetaDumpProperty property, JsonElement value)
        {
            TypeSyntax argumentTypeDeclaration = CreatePrimitivePropertyTypeDeclaration(property.Container.Type, false);

            // new MetaOptional<T>(default(T), false) | new MetaOptional<T>(nullableValue, true)
            return ObjectCreationExpression(
                GenericName(
                    Identifier(SanitizeNameOfGenericType(typeof(MetaOptional<object>))),
                    TypeArgumentList(SingletonSeparatedList(argumentTypeDeclaration))
                ),
                ArgumentList(
                    SeparatedList(
                        new ArgumentSyntax[]
                        {
                            Argument(
                                value.ValueKind is JsonValueKind.Null
                                    ? DefaultExpression(argumentTypeDeclaration)
                                    : CreateCommonInitializerSyntax(property.Container.Type, value)
                            ),
                            Argument(
                                LiteralExpression(
                                    value.ValueKind is JsonValueKind.Null
                                        ? SyntaxKind.FalseLiteralExpression
                                        : SyntaxKind.TrueLiteralExpression
                                )
                            )
                        }
                    )
                ),
                null
            );
        }

        private static ExpressionSyntax CreateCommonInitializerSyntax(
            BinPropertyType valueType,
            JsonElement defaultValue
        ) =>
            defaultValue.ValueKind switch
            {
                JsonValueKind.String
                    => LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(defaultValue.GetString())),
                JsonValueKind.Number when valueType == BinPropertyType.F32
                    => CreateFloatInitializerExpression(defaultValue.GetSingle()),
                JsonValueKind.Number
                    => LiteralExpression(
                        SyntaxKind.NumericLiteralExpression,
                        Literal($"{defaultValue.GetInt64()}", defaultValue.GetInt64())
                    ),
                JsonValueKind.Object => ImplicitObjectCreationExpression(),
                JsonValueKind.Array
                    => valueType switch
                    {
                        BinPropertyType.Vector2 => CreateVector2InitializerExpression(defaultValue.EnumerateArray()),
                        BinPropertyType.Vector3 => CreateVector3InitializerExpression(defaultValue.EnumerateArray()),
                        BinPropertyType.Vector4 => CreateVector4InitializerExpression(defaultValue.EnumerateArray()),
                        BinPropertyType.Color => CreateColorInitializerExpression(defaultValue.EnumerateArray()),
                        BinPropertyType.Matrix44 => CreateMatrix4x4InitializerExpression(defaultValue.EnumerateArray()),
                        BinPropertyType.Container => ImplicitObjectCreationExpression(),
                        BinPropertyType.UnorderedContainer => ImplicitObjectCreationExpression(),
                        _ => throw new NotImplementedException()
                    },
                _ => throw new NotImplementedException()
            };

        /* ----------------------- NUMERIC PRIMITIVE INITIALIZER CREATORS ----------------------- */
        #region Numeric Primitive Initializer Creators
        private static ExpressionSyntax CreateFloatInitializerExpression(float value) =>
            LiteralExpression(
                SyntaxKind.NumericLiteralExpression,
                Literal($"{value.ToString(NumberFormatInfo.InvariantInfo)}f", value)
            );

        private static ExpressionSyntax CreateVector2InitializerExpression(IEnumerable<JsonElement> elements) =>
            ObjectCreationExpression(
                IdentifierName(nameof(Vector2)),
                ArgumentList(
                    SeparatedList(
                        elements.Select(element => Argument(CreateFloatInitializerExpression(element.GetSingle())))
                    )
                ),
                null
            );

        private static ExpressionSyntax CreateVector3InitializerExpression(IEnumerable<JsonElement> elements) =>
            ObjectCreationExpression(
                IdentifierName(nameof(Vector3)),
                ArgumentList(
                    SeparatedList(
                        elements.Select(element => Argument(CreateFloatInitializerExpression(element.GetSingle())))
                    )
                ),
                null
            );

        private static ExpressionSyntax CreateVector4InitializerExpression(IEnumerable<JsonElement> elements) =>
            ObjectCreationExpression(
                IdentifierName(nameof(Vector4)),
                ArgumentList(
                    SeparatedList(
                        elements.Select(element => Argument(CreateFloatInitializerExpression(element.GetSingle())))
                    )
                ),
                null
            );

        private static ExpressionSyntax CreateColorInitializerExpression(IEnumerable<JsonElement> elements) =>
            ObjectCreationExpression(
                IdentifierName(nameof(Color)),
                ArgumentList(
                    SeparatedList(
                        elements.Select(element => Argument(CreateFloatInitializerExpression(element.GetSingle())))
                    )
                ),
                null
            );

        private static ExpressionSyntax CreateMatrix4x4InitializerExpression(IEnumerable<JsonElement> elements) =>
            ObjectCreationExpression(
                IdentifierName(nameof(Matrix4x4)),
                ArgumentList(
                    SeparatedList(
                        elements.SelectMany(
                            arrayElement =>
                                arrayElement
                                    .EnumerateArray()
                                    .Select(element => Argument(CreateFloatInitializerExpression(element.GetSingle())))
                        )
                    )
                ),
                null
            );
        #endregion
        /* ----------------------- NUMERIC PRIMITIVE INITIALIZER CREATORS ----------------------- */
        #endregion
        /* -------------------------------- INITIALIZER CREATORS -------------------------------- */

        /* --------------------------- GENERIC TYPE NAME SANITIZATION --------------------------- */
        #region Generic Type Name Sanitization
        private static string SanitizeNameOfGenericType(Type genericType) =>
            SanitizeNameOfGenericType(genericType.Name);

        private static string SanitizeNameOfGenericType(string nameOfGenericType) => nameOfGenericType.Split('`')[0];
        #endregion
        /* --------------------------- GENERIC TYPE NAME SANITIZATION --------------------------- */

        private static List<Type> GetRequiredTypes() =>
            new()
            {
                typeof(System.Numerics.Vector2),
                typeof(System.Numerics.Vector3),
                typeof(System.Numerics.Vector4),
                typeof(System.Numerics.Matrix4x4),
                typeof(Color),
                typeof(LeagueToolkit.Meta.MetaHash),
                typeof(LeagueToolkit.Meta.MetaObjectLink),
                typeof(LeagueToolkit.Meta.MetaWadEntryLink),
                typeof(LeagueToolkit.Meta.MetaBitBool),
                typeof(LeagueToolkit.Meta.MetaOptional<>),
                typeof(LeagueToolkit.Meta.MetaContainer<>),
                typeof(LeagueToolkit.Meta.MetaUnorderedContainer<>),
                typeof(LeagueToolkit.Meta.MetaEmbedded<>),
                typeof(System.Collections.Generic.Dictionary<,>),
                typeof(LeagueToolkit.Meta.IMetaClass),
                typeof(LeagueToolkit.Meta.Attributes.MetaClassAttribute),
                typeof(LeagueToolkit.Meta.Attributes.MetaPropertyAttribute),
                typeof(BinPropertyType)
            };

        private static List<string> GetRequiredNamespaces(List<Type> requiredTypes) =>
            requiredTypes.Select(x => x.Namespace).Distinct().ToList();

        private static IEnumerable<UsingDirectiveSyntax> TakeRequiredUsingDirectives(IEnumerable<Type> requiredTypes) =>
            requiredTypes
                .Select(x => x.Namespace)
                .Distinct()
                .OrderByDescending(x => x)
                .Select(requiredNamespace => UsingDirective(ParseName(requiredNamespace, consumeFullText: true)));

        public static MetaDump Deserialize(string dump) =>
            JsonSerializer.Deserialize<MetaDump>(
                dump,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
            );
    }
}
