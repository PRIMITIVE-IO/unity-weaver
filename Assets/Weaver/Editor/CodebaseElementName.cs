using System;
using System.Collections.Generic;
using System.Linq;

namespace Weaver.Editor
{
    public abstract class CodebaseElementName
    {
        /// <summary>
        /// A human-readable representation of the name. Examples are unqualified class names and method names without
        /// their declaring class.
        /// </summary>
        public readonly string ShortName;

        /// <summary>
        /// Backing variable assigned in constructor
        /// </summary>
        protected int hashCode;

        /// <summary>
        /// The parent element in the containment hierarchy. In particular, packages don't "contain" one another, even
        /// though they are linked in their own hierarchy.
        /// </summary>
        /// <remarks>
        /// May be <c>null</c> if there is no containing element.
        /// </remarks>

        public abstract CodebaseElementName? ContainmentParent { get; }

        protected CodebaseElementName(string shortName)
        {
            ShortName = shortName;
        }

        public virtual FileName? ContainmentFile()
        {
            if (this is FileName) return (FileName)this;
            return ContainmentParent?.ContainmentFile();
        }

        public override int GetHashCode()
        {
            return hashCode;
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj.GetType() == this.GetType() && Equals((CodebaseElementName)obj);
        }

        protected bool Equals(CodebaseElementName other)
        {
            return GetHashCode() == other.GetHashCode();
        }

        public static bool operator ==(
            CodebaseElementName a,
            CodebaseElementName b) =>
            a?.GetHashCode() == b?.GetHashCode();

        public static bool operator !=(
            CodebaseElementName a,
            CodebaseElementName b) => !(a == b);
    }


    #region MEMBERS

    public sealed class MethodName : CodebaseElementName
    {
        public override CodebaseElementName ContainmentParent => containmentParent;
        readonly CodebaseElementName containmentParent;

        public readonly string ReturnType;

        public readonly IEnumerable<Argument> Arguments;

        string? _fullyQualifiedName;

        public string FullyQualifiedName
        {
            get { return _fullyQualifiedName ??= FullyQualified(); }
        }

        public MethodName(
            CodebaseElementName parent,
            string methodName,
            string returnType,
            IEnumerable<Argument> argumentTypes) : base(methodName)
        {
            containmentParent = parent;
            ReturnType = returnType;

            Arguments = argumentTypes;
            string hashString = ShortName + ReturnType;
            foreach (Argument argumentType in argumentTypes)
            {
                hashString += argumentType.Name + argumentType.Type.Signature;
            }

            hashCode = ContainmentParent.GetHashCode() + hashString.GetHashCode();
        }

        string FullyQualified()
        {
            ClassName? parentClass = containmentParent as ClassName;
            string parentFqn = parentClass?.FullyQualifiedName ?? containmentParent.ShortName;
            return $"{parentFqn}|{ShortName}|{ReturnType}|{CommaSeparatedArguments(false)}";
        }

        string GetArgumentType(Argument argument)
        {
            if (argument.Type is ClassName argumentType)
            {
                if (!string.IsNullOrEmpty(argumentType.ContainmentPackage.PackageNameString))
                {
                    return $"{argumentType.ContainmentPackage.PackageNameString}.{argumentType.Signature}";
                }
            }

            return argument.Type.Signature;
        }

        string CommaSeparatedArguments(bool includeArgNames = true)
        {
            Func<Argument, string> argsWithoutNamesFunc = GetArgumentType;
            Func<Argument, string> argsWithNamesFunc = argument => $"{argument.Name} {GetArgumentType(argument)}";

            Func<Argument, string> argFunction = includeArgNames ? argsWithNamesFunc : argsWithoutNamesFunc;
            
            return string.Join(",", Arguments.Select(argFunction).ToList());
        }
    }
    
    #endregion

    #region TYPES

    public abstract class TypeName : CodebaseElementName
    {
        public override CodebaseElementName ContainmentParent => new PackageName();

        public string Signature;

        public static TypeName For(string signature)
        {
            if (signature.EndsWith("[]"))
            {
                return new ArrayTypeName(signature);
            }

            PrimitiveTypeName primitiveTypeName =
                PrimitiveTypeName.ForPrimitiveTypeSignature(signature);
            if (primitiveTypeName != null)
            {
                return primitiveTypeName;
            }

            string packageNameString = string.Empty;
            string classNameString = signature;
            if (signature.Contains('.'))
            {
                packageNameString = signature[..signature.LastIndexOf('.')];
                classNameString = signature[(signature.LastIndexOf('.') + 1)..];
            }

            PackageName packageName = new PackageName(packageNameString);

            return new ClassName(
                new FileName(string.Empty),
                packageName,
                classNameString);
        }

        protected TypeName(string shortName) : base(shortName)
        {
            Signature = shortName;
            hashCode = Signature.GetHashCode();
        }
    }

    public sealed class ArrayTypeName : TypeName
    {
        public ArrayTypeName(string signature) : base(signature)
        {
            Signature = signature;
            hashCode = Signature.GetHashCode();
        }
    }

    public sealed class PrimitiveTypeName : TypeName
    {
        PrimitiveTypeName(string signature) : base(signature)
        {
            Signature = signature;
            hashCode = Signature.GetHashCode();
        }


        internal static PrimitiveTypeName ForPrimitiveTypeSignature(string signature)
        {
            switch (signature.ToLowerInvariant())
            {
                case "v":
                case "void":
                case "bool":
                case "boolean":
                case "byte":
                case "char":
                case "short":
                case "int":
                case "integer":
                case "int16":
                case "int32":
                case "int64":
                case "long":
                case "float":
                case "double":
                    return new PrimitiveTypeName(signature);
                default:
                    return null;
            }
        }
    }

    #endregion

    #region CONTAINERS

    public sealed class ClassName : TypeName
    {
        public override CodebaseElementName ContainmentParent => IsOuterClass
            ? (CodebaseElementName)containmentFile
            : ParentClass;

        public override FileName ContainmentFile()
        {
            return containmentFile ?? base.ContainmentFile();
        }

        // only used if set by constructor
        readonly FileName containmentFile;

        public readonly PackageName ContainmentPackage;

        public readonly bool IsOuterClass;

        public readonly ClassName? ParentClass;

        public readonly string originalClassName;
        string _fullyQualifiedName;

        public string FullyQualifiedName
        {
            get { return _fullyQualifiedName ??= FullyQualified(); }
        }

        public ClassName(FileName containmentFile, PackageName containmentPackage, string className)
            : base(GetShortName(className))
        {
            this.containmentFile = containmentFile;
            ContainmentPackage = containmentPackage;
            originalClassName = className;

            if (!string.IsNullOrEmpty(className) && className.Contains('$'))
            {
                IsOuterClass = false;
                ParentClass = new ClassName(
                    containmentFile,
                    containmentPackage,
                    className[..className.LastIndexOf('$')]);
            }
            else
            {
                IsOuterClass = true;
            }

            hashCode = ContainmentParent.GetHashCode() + originalClassName.GetHashCode();
        }

        static string GetShortName(string className)
        {
            if (!string.IsNullOrEmpty(className) && className.Contains('$'))
            {
                string[] innerClassSplit = className.Split('$');
                return innerClassSplit.Last();
            }

            return className;
        }

        string FullyQualified()
        {
            ClassName? parentClassName = ContainmentParent as ClassName;
            if (parentClassName != null)
            {
                return $"{parentClassName.FullyQualified()}${originalClassName}";
            }

            string? pkg = ContainmentPackage.PackageNameString == string.Empty
                ? null
                : ContainmentPackage.PackageNameString;

            return IEnumerableUtils.EnumerableOfNotNull(pkg, originalClassName)
                .JoinToString(".");
        }
    }

    public sealed class FileName : CodebaseElementName
    {
        public override CodebaseElementName ContainmentParent { get; }

        public readonly string FilePath;

        public FileName(string filePath) : base(GetShortName(filePath, GetSeparator(filePath)))
        {
            FilePath = filePath;
            char separator = GetSeparator(filePath);

            ContainmentParent = filePath.Contains(separator)
                ? new PackageName(filePath[..filePath.LastIndexOf(separator)])
                : new PackageName();

            hashCode = FilePath.GetHashCode();
        }

        static string GetShortName(string path, char separator)
        {
            return path.Contains(separator)
                ? path[(path.LastIndexOf(separator) + 1)..]
                : path;
        }

        static char GetSeparator(string path)
        {
            char separator = '/';
            if (path.Contains('\\'))
            {
                separator = '\\';
            }

            return separator;
        }
    }

    public sealed class PackageName : CodebaseElementName
    {
        public readonly string ParentPackage;


        public readonly string PackageNameString;

        // these are dead-ends
        public override CodebaseElementName ContainmentParent => null;

        /// <summary>
        /// The root or zero package
        /// </summary>
        public PackageName() : base(string.Empty)
        {
            PackageNameString = string.Empty;
            hashCode = 0;
        }

        /// <summary>
        /// From a package or director path -> create a package name
        /// </summary>
        /// <param name="packageNameString">A package or directory path</param>
        public PackageName(string packageNameString) : base(GetShortName(packageNameString))
        {
            PackageNameString = packageNameString;
            ParentPackage = CreateParentPackage().PackageNameString;
            hashCode = !string.IsNullOrEmpty(packageNameString)
                ? PackageNameString.GetHashCode()
                : 0; // "" hash code does not evaluate to 0
        }

        PackageName CreateParentPackage()
        {
            if (string.IsNullOrEmpty(PackageNameString))
            {
                // the parent of the root is the root
                return new PackageName();
            }

            if (PackageNameString.Length > ShortName.Length)
            {
                // the parent is the path above this package
                // e.g. com.org.package.child ->
                //   short name:  child
                //   parent:      com.org.package
                return new PackageName(
                    PackageNameString[..(PackageNameString.Length - ShortName.Length - 1)]);
            }

            // the parent of this package is the root
            return new PackageName();
        }

        static string GetShortName(string packageName)
        {
            if (string.IsNullOrEmpty(packageName))
            {
                // root
                return string.Empty;
            }

            if (!packageName.Contains('.') && !packageName.Contains('/') && !packageName.Contains('\\'))
            {
                // top
                return packageName;
            }

            if (packageName.Contains('.'))
            {
                // a compiler FQN
                return packageName[(packageName.LastIndexOf('.') + 1)..];
            }

            if (packageName.Contains('/'))
            {
                // a path FQN
                return packageName[(packageName.LastIndexOf('/') + 1)..];
            }

            return packageName[(packageName.LastIndexOf('\\') + 1)..];
        }
    }

    #endregion

    public class Argument
    {
        public readonly string Name;
        public readonly TypeName Type;

        public Argument(string name, TypeName type)
        {
            Name = name;
            Type = type;
        }
    }
}