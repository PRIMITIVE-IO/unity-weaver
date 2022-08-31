﻿using Mono.Cecil;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace Weaver
{
    /// <summary>
    /// Used to resolve any references to assemblies that are current in the unity 
    /// project.
    /// </summary>
    public class WeaverAssemblyResolver : BaseAssemblyResolver
    {
        // map of assembly locations
        readonly IDictionary<string, string> _appDomainAssemblyLocations;
        // cache of loaded AssemblyDefinitions
        readonly IDictionary<string, AssemblyDefinition> _cache;

        public WeaverAssemblyResolver()
        {
            // Caches
            _appDomainAssemblyLocations = new Dictionary<string, string>();
            _cache = new Dictionary<string, AssemblyDefinition>();
            // Get the current app domain
            AppDomain domain = AppDomain.CurrentDomain;
            // Find all assemblies
            Assembly[] assemblies = domain.GetAssemblies();
            // for each currently loaded assembly,
            foreach (Assembly assembly in assemblies)
            {
                // Dynamic assemblies don't live on disk. 
                if (assembly.ReflectionOnly || assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location))
                    continue;

                // store locations
                _appDomainAssemblyLocations[assembly.FullName] = assembly.Location;
                // add all directories as search paths
                AddSearchDirectory(System.IO.Path.GetDirectoryName(assembly.Location));
            }
        }

        public override AssemblyDefinition Resolve(AssemblyNameReference name)
        {
            AssemblyDefinition assemblyDef = FindAssemblyDefinition(name.FullName, null);

            if (assemblyDef == null)
            {
                assemblyDef = base.Resolve(name);
                _cache[name.FullName] = assemblyDef;
            }

            return assemblyDef;
        }

        public override AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
        {
            AssemblyDefinition assemblyDef = FindAssemblyDefinition(name.FullName, parameters);

            if (assemblyDef == null)
            {
                assemblyDef = base.Resolve(name, parameters);
                _cache[name.FullName] = assemblyDef;
            }

            return assemblyDef;
        }

        /// <summary>
        /// Searches for AssemblyDefinition in our cache, and failing that,
        /// looks for a known location.  Returns null if both attempts fail.
        /// </summary>
        AssemblyDefinition FindAssemblyDefinition(string fullName, ReaderParameters parameters)
        {
            if (fullName == null)
            {
                throw new ArgumentNullException(nameof(fullName));
            }

            // Look in cache first
            if (_cache.TryGetValue(fullName, out AssemblyDefinition assemblyDefinition))
            {
                return assemblyDefinition;
            }

            // Try to use known location
            if (_appDomainAssemblyLocations.TryGetValue(fullName, out string location))
            {
                // Ready the assembly off disk.
                assemblyDefinition = parameters != null 
                    ? AssemblyDefinition.ReadAssembly(location, parameters) 
                    : AssemblyDefinition.ReadAssembly(location);

                _cache[fullName] = assemblyDefinition;

                return assemblyDefinition;
            }

            return null;
        }
    }
}
