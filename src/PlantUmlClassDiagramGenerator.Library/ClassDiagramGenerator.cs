﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PlantUmlClassDiagramGenerator.Library
{
    public class ClassDiagramGenerator : CSharpSyntaxWalker
    {
        private readonly HashSet<string> types = new HashSet<string>();
        private readonly IList<SyntaxNode> additionalTypeDeclarationNodes;
        private readonly Accessibilities ignoreMemberAccessibilities;
        private readonly RelationshipCollection relationships = new RelationshipCollection();
        private readonly TextWriter writer;
        private readonly string indent;
        private int nestingDepth = 0;
        private readonly bool createAssociation;
        private readonly bool excludeInheritance;

        private readonly Dictionary<string, string> escapeDictionary = new Dictionary<string, string>
        {
            {@"(?<before>[^{]){(?<after>{[^{])", "${before}&#123;${after}"},
            {@"(?<before>[^}])}(?<after>[^}])", "${before}&#125;${after}"},
        };

        private readonly List<string> systemTypes =
            typeof(string)
                .Assembly
                .GetTypes()
                .Where(t => t.Namespace != null && t.Namespace.StartsWith("System"))
                .Select(t => t.Name)
                .OrderBy(t => t)
                .ToList();

        public ClassDiagramGenerator(
            TextWriter writer, 
            string indent, 
            Accessibilities ignoreMemberAccessibilities = Accessibilities.None, 
            bool createAssociation = true,
            bool excludeInheritance = false)
        {
            this.writer = writer;
            this.indent = indent;
            additionalTypeDeclarationNodes = new List<SyntaxNode>();
            this.ignoreMemberAccessibilities = ignoreMemberAccessibilities;
            this.createAssociation = createAssociation;
            this.excludeInheritance = excludeInheritance;
        }

        public void Generate(SyntaxNode root)
        {
            WriteLine("@startuml");
            GenerateInternal(root);
            WriteLine("@enduml");
        }

        public void GenerateInternal(SyntaxNode root)
        {
            Visit(root);
            GenerateAdditionalTypeDeclarations();
            GenerateRelationships();
        }

        public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
        {
            VisitTypeDeclaration(node, () => base.VisitInterfaceDeclaration(node));
        }

        public override void VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            VisitTypeDeclaration(node, () => base.VisitClassDeclaration(node));
        }

        public override void VisitStructDeclaration(StructDeclarationSyntax node)
        {
            if (SkipInnerTypeDeclaration(node)) { return; }

            relationships.AddInnerclassRelationFrom(node);

            if (!excludeInheritance)
            {
                relationships.AddInheritanceFrom(node);    
            }
  
            var typeName = TypeNameText.From(node);
            var name = typeName.Identifier;
            var typeParam = typeName.TypeArguments;
            var type = $"{name}{typeParam}";

            types.Add(name);

            WriteLine($"class {type} <<struct>> {{");

            nestingDepth++;
            base.VisitStructDeclaration(node);
            nestingDepth--;

            WriteLine("}");
        }

        public override void VisitEnumDeclaration(EnumDeclarationSyntax node)
        {
            if (SkipInnerTypeDeclaration(node)) { return; }

            relationships.AddInnerclassRelationFrom(node);

            var type = $"{node.Identifier}";

            types.Add(type);

            WriteLine($"{node.EnumKeyword} {type} {{");

            nestingDepth++;
            base.VisitEnumDeclaration(node);
            nestingDepth--;

            WriteLine("}");
        }

        public override void VisitFieldDeclaration(FieldDeclarationSyntax node)
        {
            if (IsIgnoreMember(node.Modifiers)) { return; }

            var modifiers = GetMemberModifiersText(node.Modifiers);
            var type = node.Declaration.Type;
            var variables = node.Declaration.Variables;
            var parentClass = (node.Parent as TypeDeclarationSyntax);
            var isTypeParameterField = parentClass?.TypeParameterList?.Parameters
                .Any(t => t.Identifier.Text == type.ToString()) ?? false;

            foreach (var field in variables)
            {
                Type fieldType = type.GetType();
                if (!createAssociation || fieldType == typeof(PredefinedTypeSyntax) || fieldType == typeof(NullableTypeSyntax) || isTypeParameterField)
                {
                    var useLiteralInit = field.Initializer?.Value?.Kind().ToString().EndsWith("LiteralExpression") ?? false;
                    var initValue = useLiteralInit
                        ? (" = " + escapeDictionary.Aggregate(field.Initializer.Value.ToString(),
                            (f, e) => Regex.Replace(f, e.Key, e.Value)))
                        : "";
                    WriteLine($"{modifiers}{field.Identifier} : {type}{initValue}");
                }
                else
                {
                    if (fieldType == typeof(GenericNameSyntax))
                    {
                        additionalTypeDeclarationNodes.Add(type);
                    }
                    relationships.AddAssociationFrom(node, field);
                }
            }
        }

        public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
        {
            if (IsIgnoreMember(node.Modifiers)) { return; }

            var type = node.Type;

            var parentClass = (node.Parent as TypeDeclarationSyntax);
            var isTypeParameterProp = parentClass?.TypeParameterList?.Parameters
                .Any(t => t.Identifier.Text == type.ToString()) ?? false;
            
            if (!createAssociation 
                || type.GetType() == typeof(PredefinedTypeSyntax)
                || isTypeParameterProp
                || systemTypes.Contains(type.ToString().Replace("?", string.Empty)))
            {
                var modifiers = GetMemberModifiersText(node.Modifiers);
                var name = node.Identifier.ToString();

                //Property does not have an accessor is an expression-bodied property. (get only)
                var accessorStr = "<<get>>";
                if (node.AccessorList != null)
                {
                    var accessor = node.AccessorList.Accessors
                        .Where(x => !x.Modifiers.Select(y => y.Kind()).Contains(SyntaxKind.PrivateKeyword))
                        .Select(x => $"<<{(x.Modifiers.ToString() == "" ? "" : (x.Modifiers.ToString() + " "))}{x.Keyword}>>");
                    accessorStr = string.Join(" ", accessor);
                }
                var useLiteralInit = node.Initializer?.Value?.Kind().ToString().EndsWith("LiteralExpression") ?? false;
                var initValue = useLiteralInit
                    ? (" = " + escapeDictionary.Aggregate(node.Initializer.Value.ToString(),
                        (n, e) => Regex.Replace(n, e.Key, e.Value)))
                    : "";

                WriteLine($"{modifiers}{name} : {type} {accessorStr}{initValue}");
            }
            else
            {
                if (type.GetType() == typeof(GenericNameSyntax))
                {
                    additionalTypeDeclarationNodes.Add(type);
                }
                relationships.AddAssociationFrom(node);
            }
        }

        public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            if (IsIgnoreMember(node.Modifiers)) { return; }

            var modifiers = GetMemberModifiersText(node.Modifiers);
            var name = node.Identifier.ToString();
            var returnType = node.ReturnType.ToString();
            var args = node.ParameterList.Parameters.Select(p => $"{p.Identifier}:{p.Type}");

            WriteLine($"{modifiers}{name}({string.Join(", ", args)}) : {returnType}");
        }

        public override void VisitEnumMemberDeclaration(EnumMemberDeclarationSyntax node)
        {
            WriteLine($"{node.Identifier}{node.EqualsValue},");
        }

        public override void VisitEventFieldDeclaration(EventFieldDeclarationSyntax node)
        {
            if (IsIgnoreMember(node.Modifiers)) { return; }

            var modifiers = GetMemberModifiersText(node.Modifiers);
            var name = string.Join(",", node.Declaration.Variables.Select(v => v.Identifier));
            var typeName = node.Declaration.Type.ToString();

            WriteLine($"{modifiers} <<{node.EventKeyword}>> {name} : {typeName} ");
        }

        public override void VisitGenericName(GenericNameSyntax node)
        {
            if (createAssociation)
            {
                additionalTypeDeclarationNodes.Add(node);
            }
        }

        private void WriteLine(string line)
        {
            var space = string.Concat(Enumerable.Repeat(indent, nestingDepth));
            writer.WriteLine(space + line.Replace("<<", "<").Replace(">>", ">"));
        }

        private bool SkipInnerTypeDeclaration(SyntaxNode node)
        {
            if (nestingDepth <= 0) return false;

            additionalTypeDeclarationNodes.Add(node);
            return true;
        }

        private void GenerateAdditionalTypeDeclarations()
        {
            for (int i = 0; i < additionalTypeDeclarationNodes.Count; i++)
            {
                SyntaxNode node = additionalTypeDeclarationNodes[i];
                if (node is GenericNameSyntax genericNode)
                {
                    if (createAssociation)
                    {
                        GenerateAdditionalGenericTypeDeclaration(genericNode);
                    }
                    continue;
                }
                Visit(node);
            }
        }

        private void GenerateAdditionalGenericTypeDeclaration(GenericNameSyntax genericNode)
        {
            var typename = TypeNameText.From(genericNode);

            if (systemTypes.Contains(typename.Identifier.Replace("\"", "")))
            {
                return;
            }
            
            if (!types.Contains(typename.Identifier))
            {
                WriteLine($"class {typename.Identifier}{typename.TypeArguments} {{");
                WriteLine("}");
                types.Add(typename.Identifier);
            }
        }

        private void GenerateRelationships()
        {
            foreach (var relationship in relationships)
            {
                WriteLine(relationship.ToString());
            }
        }

        private void VisitTypeDeclaration(TypeDeclarationSyntax node, Action visitBase)
        {
            if (SkipInnerTypeDeclaration(node)) { return; }
            
            relationships.AddInnerclassRelationFrom(node);
            if (!excludeInheritance)
            {
                relationships.AddInheritanceFrom(node);    
            }

            var modifiers = GetTypeModifiersText(node.Modifiers);
            var keyword = (node.Modifiers.Any(SyntaxKind.AbstractKeyword) ? "abstract " : "")
                + node.Keyword.ToString();

            var typeName = TypeNameText.From(node);
            var name = typeName.Identifier;
            var typeParam = typeName.TypeArguments;
            var type = $"{name}{typeParam}";

            types.Add(name);

            WriteLine($"{keyword} {type} {modifiers}{{");

            nestingDepth++;
            visitBase();
            nestingDepth--;

            WriteLine("}");
        }

        private string GetTypeModifiersText(SyntaxTokenList modifiers)
        {
            var tokens = modifiers.Select(token =>
            {
                switch (token.Kind())
                {
                    case SyntaxKind.PublicKeyword:
                    case SyntaxKind.PrivateKeyword:
                    case SyntaxKind.ProtectedKeyword:
                    case SyntaxKind.InternalKeyword:
                    case SyntaxKind.AbstractKeyword:
                        return "";
                    default:
                        return $"<<{token.ValueText}>>";
                }
            }).Where(token => token != "");

            var result = string.Join(" ", tokens);
            if (result != string.Empty)
            {
                result += " ";
            };
            return result;
        }

        private bool IsIgnoreMember(SyntaxTokenList modifiers)
        {
            if (ignoreMemberAccessibilities == Accessibilities.None) { return false; }

            var tokenKinds = modifiers.Select(x => x.Kind()).ToArray();

            if (ignoreMemberAccessibilities.HasFlag(Accessibilities.ProtectedInternal)
                && tokenKinds.Contains(SyntaxKind.ProtectedKeyword)
                && tokenKinds.Contains(SyntaxKind.InternalKeyword))
            {
                return true;
            }

            if (ignoreMemberAccessibilities.HasFlag(Accessibilities.Public)
                && tokenKinds.Contains(SyntaxKind.PublicKeyword))
            {
                return true;
            }

            if (ignoreMemberAccessibilities.HasFlag(Accessibilities.Protected)
                && tokenKinds.Contains(SyntaxKind.ProtectedKeyword))
            {
                return true;
            }

            if (ignoreMemberAccessibilities.HasFlag(Accessibilities.Internal)
                && tokenKinds.Contains(SyntaxKind.InternalKeyword))
            {
                return true;
            }

            if (ignoreMemberAccessibilities.HasFlag(Accessibilities.Private)
                && tokenKinds.Contains(SyntaxKind.PrivateKeyword))
            {
                return true;
            }
            return false;
        }

        private string GetMemberModifiersText(SyntaxTokenList modifiers)
        {
            var tokens = modifiers.Select(token =>
            {
                switch (token.Kind())
                {
                    case SyntaxKind.PublicKeyword:
                        return "+";
                    case SyntaxKind.PrivateKeyword:
                        return "-";
                    case SyntaxKind.ProtectedKeyword:
                        return "#";
                    case SyntaxKind.AbstractKeyword:
                    case SyntaxKind.StaticKeyword:
                        return $"{{{token.ValueText}}}";
                    case SyntaxKind.InternalKeyword:
                    default:
                        return $"<<{token.ValueText}>>";
                }
            });
            var result = string.Join(" ", tokens);
            if (result != string.Empty)
            {
                result += " ";
            };
            return result;
        }
    }

}
