// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

using Internal.TypeSystem;

namespace Internal.TypeSystem.Ecma
{
    public sealed partial class EcmaModule : ModuleDesc, IAssemblyDesc
    {
        private PEReader _peReader;
        private MetadataReader _metadataReader;

        private AssemblyDefinition _assemblyDefinition;

        internal interface IEntityHandleObject
        {
            EntityHandle Handle
            {
                get;
            }
        }

        private sealed class EcmaObjectLookupWrapper : IEntityHandleObject
        {
            private EntityHandle _handle;
            private object _obj;

            public EcmaObjectLookupWrapper(EntityHandle handle, object obj)
            {
                _obj = obj;
                _handle = handle;
            }

            public EntityHandle Handle
            {
                get
                {
                    return _handle;
                }
            }

            public object Object
            {
                get
                {
                    return _obj;
                }
            }
        }

        internal class EcmaObjectLookupHashtable : LockFreeReaderHashtable<EntityHandle, IEntityHandleObject>
        {
            private EcmaModule _module;

            public EcmaObjectLookupHashtable(EcmaModule module)
            {
                _module = module;
            }

            protected override int GetKeyHashCode(EntityHandle key)
            {
                return key.GetHashCode();
            }

            protected override int GetValueHashCode(IEntityHandleObject value)
            {
                return value.Handle.GetHashCode();
            }

            protected override bool CompareKeyToValue(EntityHandle key, IEntityHandleObject value)
            {
                return key.Equals(value.Handle);
            }

            protected override bool CompareValueToValue(IEntityHandleObject value1, IEntityHandleObject value2)
            {
                if (Object.ReferenceEquals(value1, value2))
                    return true;
                else
                    return value1.Handle.Equals(value2.Handle);
            }

            protected override IEntityHandleObject CreateValueFromKey(EntityHandle handle)
            {
                object item;
                switch (handle.Kind)
                {
                    case HandleKind.TypeDefinition:
                        item = new EcmaType(_module, (TypeDefinitionHandle)handle);
                        break;

                    case HandleKind.MethodDefinition:
                        {
                            MethodDefinitionHandle methodDefinitionHandle = (MethodDefinitionHandle)handle;
                            TypeDefinitionHandle typeDefinitionHandle = _module._metadataReader.GetMethodDefinition(methodDefinitionHandle).GetDeclaringType();
                            EcmaType type = (EcmaType)_module.GetObject(typeDefinitionHandle);
                            item = new EcmaMethod(type, methodDefinitionHandle);
                        }
                        break;

                    case HandleKind.FieldDefinition:
                        {
                            FieldDefinitionHandle fieldDefinitionHandle = (FieldDefinitionHandle)handle;
                            TypeDefinitionHandle typeDefinitionHandle = _module._metadataReader.GetFieldDefinition(fieldDefinitionHandle).GetDeclaringType();
                            EcmaType type = (EcmaType)_module.GetObject(typeDefinitionHandle);
                            item = new EcmaField(type, fieldDefinitionHandle);
                        }
                        break;

                    case HandleKind.TypeReference:
                        item = _module.ResolveTypeReference((TypeReferenceHandle)handle);
                        break;

                    case HandleKind.MemberReference:
                        item = _module.ResolveMemberReference((MemberReferenceHandle)handle);
                        break;

                    case HandleKind.AssemblyReference:
                        item = _module.ResolveAssemblyReference((AssemblyReferenceHandle)handle);
                        break;

                    case HandleKind.TypeSpecification:
                        item = _module.ResolveTypeSpecification((TypeSpecificationHandle)handle);
                        break;

                    case HandleKind.MethodSpecification:
                        item = _module.ResolveMethodSpecification((MethodSpecificationHandle)handle);
                        break;

                    case HandleKind.ExportedType:
                        item = _module.ResolveExportedType((ExportedTypeHandle)handle);
                        break;

                    case HandleKind.StandaloneSignature:
                        item = _module.ResolveStandaloneSignature((StandaloneSignatureHandle)handle);
                        break;

                    // TODO: Resolve other tokens
                    default:
                        throw new BadImageFormatException("Unknown metadata token type: " + handle.Kind);
                }

                switch (handle.Kind)
                {
                    case HandleKind.TypeDefinition:
                    case HandleKind.MethodDefinition:
                    case HandleKind.FieldDefinition:
                        // type/method/field definitions directly correspond to their target item.
                        return (IEntityHandleObject)item;
                    default:
                        // Everything else is some form of reference which cannot be self-describing
                        return new EcmaObjectLookupWrapper(handle, item);
                }
            }
        }

        private LockFreeReaderHashtable<EntityHandle, IEntityHandleObject> _resolvedTokens;

        public EcmaModule(TypeSystemContext context, PEReader peReader)
            : base(context)
        {
            _peReader = peReader;

            var stringDecoderProvider = context as IMetadataStringDecoderProvider;

            _metadataReader = peReader.GetMetadataReader(MetadataReaderOptions.None /* MetadataReaderOptions.ApplyWindowsRuntimeProjections */,
                (stringDecoderProvider != null) ? stringDecoderProvider.GetMetadataStringDecoder() : null);

            _assemblyDefinition = _metadataReader.GetAssemblyDefinition();

            _resolvedTokens = new EcmaObjectLookupHashtable(this);
        }

        public EcmaModule(TypeSystemContext context, MetadataReader metadataReader)
            : base(context)
        {
            _metadataReader = metadataReader;
        }

        public PEReader PEReader
        {
            get
            {
                return _peReader;
            }
        }

        public MetadataReader MetadataReader
        {
            get
            {
                return _metadataReader;
            }
        }

        public AssemblyDefinition AssemblyDefinition
        {
            get
            {
                return _assemblyDefinition;
            }
        }

        public override MetadataType GetType(string nameSpace, string name, bool throwIfNotFound = true)
        {
            var stringComparer = _metadataReader.StringComparer;

            // TODO: More efficient implementation?
            foreach (var typeDefinitionHandle in _metadataReader.TypeDefinitions)
            {
                var typeDefinition = _metadataReader.GetTypeDefinition(typeDefinitionHandle);
                if (stringComparer.Equals(typeDefinition.Name, name) &&
                    stringComparer.Equals(typeDefinition.Namespace, nameSpace))
                {
                    return (MetadataType)GetType((EntityHandle)typeDefinitionHandle);
                }
            }

            foreach (var exportedTypeHandle in _metadataReader.ExportedTypes)
            {
                var exportedType = _metadataReader.GetExportedType(exportedTypeHandle);
                if (stringComparer.Equals(exportedType.Name, name) &&
                    stringComparer.Equals(exportedType.Namespace, nameSpace))
                {
                    if (exportedType.IsForwarder)
                    {
                        Object implementation = GetObject(exportedType.Implementation);

                        if (implementation is EcmaModule)
                        {
                            return ((EcmaModule)(implementation)).GetType(nameSpace, name);
                        }

                        // TODO
                        throw new NotImplementedException();
                    }
                    // TODO:
                    throw new NotImplementedException();
                }
            }

            if (throwIfNotFound)
                throw CreateTypeLoadException(nameSpace + "." + name);

            return null;
        }

        public Exception CreateTypeLoadException(string fullTypeName)
        {
            return new TypeLoadException(String.Format("Could not load type '{0}' from assembly '{1}'.", fullTypeName, this.ToString()));
        }

        public TypeDesc GetType(EntityHandle handle)
        {
            TypeDesc type = GetObject(handle) as TypeDesc;
            if (type == null)
                throw new BadImageFormatException("Type expected");
            return type;
        }

        public MethodDesc GetMethod(EntityHandle handle)
        {
            MethodDesc method = GetObject(handle) as MethodDesc;
            if (method == null)
                throw new BadImageFormatException("Method expected");
            return method;
        }

        public FieldDesc GetField(EntityHandle handle)
        {
            FieldDesc field = GetObject(handle) as FieldDesc;
            if (field == null)
                throw new BadImageFormatException("Field expected");
            return field;
        }

        public Object GetObject(EntityHandle handle)
        {
            IEntityHandleObject obj = _resolvedTokens.GetOrCreateValue(handle);
            if (obj is EcmaObjectLookupWrapper)
            {
                return ((EcmaObjectLookupWrapper)obj).Object;
            }
            else
            {
                return obj;
            }
        }

        private Object ResolveMethodSpecification(MethodSpecificationHandle handle)
        {
            MethodSpecification methodSpecification = _metadataReader.GetMethodSpecification(handle);

            MethodDesc methodDef = GetMethod(methodSpecification.Method);

            BlobReader signatureReader = _metadataReader.GetBlobReader(methodSpecification.Signature);
            EcmaSignatureParser parser = new EcmaSignatureParser(this, signatureReader);

            TypeDesc[] instantiation = parser.ParseMethodSpecSignature();
            return Context.GetInstantiatedMethod(methodDef, new Instantiation(instantiation));
        }

        private Object ResolveStandaloneSignature(StandaloneSignatureHandle handle)
        {
            StandaloneSignature signature = _metadataReader.GetStandaloneSignature(handle);
            BlobReader signatureReader = _metadataReader.GetBlobReader(signature.Signature);
            EcmaSignatureParser parser = new EcmaSignatureParser(this, signatureReader);

            MethodSignature methodSig = parser.ParseMethodSignature();
            return methodSig;
        }

        private Object ResolveTypeSpecification(TypeSpecificationHandle handle)
        {
            TypeSpecification typeSpecification = _metadataReader.GetTypeSpecification(handle);

            BlobReader signatureReader = _metadataReader.GetBlobReader(typeSpecification.Signature);
            EcmaSignatureParser parser = new EcmaSignatureParser(this, signatureReader);

            return parser.ParseType();
        }

        private Object ResolveMemberReference(MemberReferenceHandle handle)
        {
            MemberReference memberReference = _metadataReader.GetMemberReference(handle);

            Object parent = GetObject(memberReference.Parent);

            TypeDesc parentTypeDesc = parent as TypeDesc;
            if (parentTypeDesc != null)
            {
                BlobReader signatureReader = _metadataReader.GetBlobReader(memberReference.Signature);

                EcmaSignatureParser parser = new EcmaSignatureParser(this, signatureReader);

                string name = _metadataReader.GetString(memberReference.Name);

                if (parser.IsFieldSignature)
                {
                    FieldDesc field = parentTypeDesc.GetField(name);
                    if (field != null)
                        return field;

                    // TODO: Better error message
                    throw new MissingMemberException("Field not found " + parent.ToString() + "." + name);
                }
                else
                {
                    MethodSignature sig = parser.ParseMethodSignature();
                    TypeDesc typeDescToInspect = parentTypeDesc;

                    // Try to resolve the name and signature in the current type, or any of the base types.
                    do
                    {
                        // TODO: handle substitutions
                        MethodDesc method = typeDescToInspect.GetMethod(name, sig);
                        if (method != null)
                        {
                            // If this resolved to one of the base types, make sure it's not a constructor.
                            // Instance constructors are not inherited.
                            if (typeDescToInspect != parentTypeDesc && method.IsConstructor)
                                break;

                            return method;
                        }
                        typeDescToInspect = typeDescToInspect.BaseType;
                    } while (typeDescToInspect != null);

                    // TODO: Better error message
                    throw new MissingMemberException("Method not found " + parent.ToString() + "." + name);
                }
            }
            else if (parent is MethodDesc)
            {
                throw new NotSupportedException("Vararg methods not supported in .NET Core.");
            }
            else if (parent is ModuleDesc)
            {
                throw new NotImplementedException("MemberRef to a global function or variable.");
            }

            throw new BadImageFormatException();
        }

        private Object ResolveTypeReference(TypeReferenceHandle handle)
        {
            TypeReference typeReference = _metadataReader.GetTypeReference(handle);

            Object resolutionScope = GetObject(typeReference.ResolutionScope);

            if (resolutionScope is EcmaModule)
            {
                return ((EcmaModule)(resolutionScope)).GetType(_metadataReader.GetString(typeReference.Namespace), _metadataReader.GetString(typeReference.Name));
            }
            else
            if (resolutionScope is EcmaType)
            {
                return ((EcmaType)(resolutionScope)).GetNestedType(_metadataReader.GetString(typeReference.Name));
            }

            // TODO
            throw new NotImplementedException();
        }

        private Object ResolveAssemblyReference(AssemblyReferenceHandle handle)
        {
            AssemblyReference assemblyReference = _metadataReader.GetAssemblyReference(handle);

            AssemblyName an = new AssemblyName();
            an.Name = _metadataReader.GetString(assemblyReference.Name);
            an.Version = assemblyReference.Version;

            var publicKeyOrToken = _metadataReader.GetBlobBytes(assemblyReference.PublicKeyOrToken);
            if ((an.Flags & AssemblyNameFlags.PublicKey) != 0)
            {
                an.SetPublicKey(publicKeyOrToken);
            }
            else
            {
                an.SetPublicKeyToken(publicKeyOrToken);
            }

            an.CultureName = _metadataReader.GetString(assemblyReference.Culture);
            an.ContentType = GetContentTypeFromAssemblyFlags(assemblyReference.Flags);

            return Context.ResolveAssembly(an);
        }

        private Object ResolveExportedType(ExportedTypeHandle handle)
        {
            ExportedType exportedType = _metadataReader.GetExportedType(handle);

            var implementation = GetObject(exportedType.Implementation);
            if (implementation is EcmaModule)
            {
                var module = (EcmaModule)implementation;
                string nameSpace = _metadataReader.GetString(exportedType.Namespace);
                string name = _metadataReader.GetString(exportedType.Name);
                return module.GetType(nameSpace, name);
            }
            else
            if (implementation is TypeDesc)
            {
                var type = (EcmaType)implementation;
                string name = _metadataReader.GetString(exportedType.Name);
                var nestedType = type.GetNestedType(name);
                // TODO: Better error message
                if (nestedType == null)
                    throw new TypeLoadException("Nested type not found " + type.ToString() + "." + name);
                return nestedType;
            }
            else
            {
                throw new BadImageFormatException("Unknown metadata token type for exported type");
            }
        }

        public override IEnumerable<MetadataType> GetAllTypes()
        {
            foreach (var typeDefinitionHandle in _metadataReader.TypeDefinitions)
            {
                yield return (MetadataType)GetType(typeDefinitionHandle);
            }
        }

        public override TypeDesc GetGlobalModuleType()
        {
            int typeDefinitionsCount = _metadataReader.TypeDefinitions.Count;
            if (typeDefinitionsCount == 0)
                return null;

            return GetType(MetadataTokens.EntityHandle(0x02000001 /* COR_GLOBAL_PARENT_TOKEN */));
        }

        private static AssemblyContentType GetContentTypeFromAssemblyFlags(AssemblyFlags flags)
        {
            return (AssemblyContentType)(((int)flags & 0x0E00) >> 9);
        }

        private AssemblyName _assemblyName;

        // Returns cached copy of the name. Caller has to create a clone before mutating the name.
        public AssemblyName GetName()
        {
            if (_assemblyName == null)
            {
                AssemblyName an = new AssemblyName();
                an.Name = _metadataReader.GetString(_assemblyDefinition.Name);
                an.Version = _assemblyDefinition.Version;
                an.SetPublicKey(_metadataReader.GetBlobBytes(_assemblyDefinition.PublicKey));

                an.CultureName = _metadataReader.GetString(_assemblyDefinition.Culture);
                an.ContentType = GetContentTypeFromAssemblyFlags(_assemblyDefinition.Flags);

                _assemblyName = an;
            }

            return _assemblyName;
        }

        public string GetUserString(UserStringHandle userStringHandle)
        {
            // String literals are not cached
            return _metadataReader.GetUserString(userStringHandle);
        }

        public bool HasCustomAttribute(string attributeNamespace, string attributeName)
        {
            return _metadataReader.GetCustomAttributeHandle(_assemblyDefinition.GetCustomAttributes(),
                attributeNamespace, attributeName).IsNil;
        }

        public override string ToString()
        {
            return GetName().FullName;
        }
    }
}
