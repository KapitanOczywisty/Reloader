using Verse;
using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Harmony;
using System.Reflection.Emit;

namespace Reloader
{
	[AttributeUsage(AttributeTargets.Method)]
	public class ReloadMethod : Attribute
	{
		public ReloadMethod() { }
	}

	class Reloader : Mod
	{

		Dictionary<string, MethodInfo> reloadableMethods = new Dictionary<string, MethodInfo>();
		static Dictionary<string, Assembly> originalAssemblies = new Dictionary<string, Assembly>();
		ModContentPack content;
		HarmonyInstance harmony;
		HarmonyMethod transpiler;

		public Reloader(ModContentPack content) : base(content)
		{
			harmony = HarmonyInstance.Create("Reloader.Unstable");
			transpiler = new HarmonyMethod(AccessTools.Method(typeof(Reloader), "TranslationTranspiler"));

			var modDirName = Path.GetDirectoryName(content.RootDir).ToLower();

			this.content = content;
			LongEventHandler.QueueLongEvent(CacheExstingMethods, "CacheExstingMethods", false, null);

			var folderPath = Path.Combine(content.RootDir, "Assemblies");
			var watcher = new FileSystemWatcher()
			{
				Path = folderPath,
				Filter = "*.dll",
				NotifyFilter = NotifyFilters.CreationTime
				| NotifyFilters.LastWrite
				| NotifyFilters.FileName
				| NotifyFilters.DirectoryName
			};
			var handler = new FileSystemEventHandler((sender, args) =>
			{
				var path = args.FullPath;
				if (ExcludedDLLs(path)) return;
				LoadPath(path);
			});
			watcher.Created += handler;
			watcher.Changed += handler;
			watcher.EnableRaisingEvents = true;
		}

		void CacheExstingMethods()
		{

			AppDomain.CurrentDomain.GetAssemblies()
				.Where(assembly =>
				{
					var name = assembly.FullName;
					name = name.Split(',')[0];
					var dllPath = Path.Combine(Path.Combine(content.RootDir, "Assemblies"), name + ".dll");
					return File.Exists(dllPath) && ExcludedDLLs(dllPath) == false;
				})
				.ToList()
				.ForEach(assembly =>
				{
					Log.Warning("Reloader: analyzing " + assembly.FullName);
					var name = assembly.FullName;
					name = name.Split(',')[0];
					originalAssemblies[name] = assembly;
					assembly.GetTypes().ToList()
						.ForEach(type => type.GetMethods(allBindings)
							.ToList()
							.ForEach(method =>
							{
								ReloadMethod attr;
								if (method.TryGetAttribute(out attr))
								{

									var key = method.DeclaringType.FullName + "." + method.Name;
									if (method.IsGenericMethodDefinition)
									{
										Log.Error($"Reloader: Cannot reload generic method definition {key} - skipping");
										return;
									}
									reloadableMethods[key] = method;
									var methodType = method.DeclaringType;
									Log.Warning("Reloader: found reloadable method " + key);
								}
							})
						);
				});
		}

		public static T FindOldMember<T>(T[] elements, T target) {
			return elements.ToList().Find(c => c.ToString() == target.ToString());
		}

		public static bool ReplaceMember<T>(T found, CodeInstruction code) {
			if(found != null)
			{
				//code = new CodeInstruction(code.opcode, found);
				code.operand = found;
				return true;
			}
			else
			{
				Log.Error("Reloader: MemeberNotFound!");
				return false;
			}
		}

		static IEnumerable<CodeInstruction> TranslationTranspiler(IEnumerable<CodeInstruction> instructions)
		{
			var codes = new List<CodeInstruction>(instructions);

			Log.Warning("Transpiler on! "+codes.Count);

			for (int i = 0; i < codes.Count; i++)
			{
				Log.Message(codes[i] +"", true);

				var member = codes[i].operand as MemberInfo;
				if (member == null)
					continue;

				var name = member.Module.Assembly.FullName.Split(',')[0];

				if (!originalAssemblies.ContainsKey(name))
					continue;

				Assembly assembly = originalAssemblies[name];

				//Log.Warning("MemberInfo / ToString:" + member.Module.ToString() + " / AssemblyName: " + name + " / MemberType" + member.MemberType.ToString(), true);

				var type = member.MemberType;
				
				// Type
				if (type == MemberTypes.TypeInfo)
				{
					Type found = assembly.GetType((codes[i].operand as Type).FullName);
					ReplaceMember(found, codes[i]);
					continue;
				}

				var parent = assembly.GetType(member.DeclaringType.FullName);

				// MethodInfo
				if (type == MemberTypes.Method)
				{
					var found = FindOldMember(parent.GetMethods(), codes[i].operand as MethodInfo);
					ReplaceMember(found, codes[i]);
				}
				// FieldInfo
				else if (type == MemberTypes.Field)
				{
					var found = FindOldMember(parent.GetFields(), codes[i].operand as FieldInfo);
					ReplaceMember(found, codes[i]);
				}
				// PropertyInfo
				else if (type == MemberTypes.Property)
				{
					var found = FindOldMember(parent.GetProperties(), codes[i].operand as PropertyInfo);
					ReplaceMember(found, codes[i]);
				}
				// EventInfo
				else if (type == MemberTypes.Event)
				{
					var found = FindOldMember(parent.GetEvents(), codes[i].operand as EventInfo);
					ReplaceMember(found, codes[i]);
				}
				// ConstructorInfo
				else if (type == MemberTypes.Constructor)
				{
					var found = FindOldMember(parent.GetConstructors(), codes[i].operand as ConstructorInfo);
					ReplaceMember(found, codes[i]);
				}

				else
				{
					Log.Error("Unknown member "+ type);
				}





			}
			
			return codes.AsEnumerable();
		}

		void LoadPath(string path)
		{

			var assembly = Assembly.Load(File.ReadAllBytes(path));
			assembly.GetTypes().ToList()
				.ForEach(type => type.GetMethods(allBindings)
					.ToList()
					.ForEach(newMethod =>
					{
						ReloadMethod attr;
						if (newMethod.TryGetAttribute(out attr) && !newMethod.IsGenericMethodDefinition)
						{
							var key = newMethod.DeclaringType.FullName + "." + newMethod.Name;
							Log.Warning("Reloader: patching " + key);

							var originalMethod = reloadableMethods[key];
							if (originalMethod != null)
							{
								var originalCodeStart = Memory.GetMethodStart(originalMethod, out Exception ex1);
								if (ex1 != null)
								{
									Log.Warning($"Reloader: exception getting original method: {ex1.Message}");
									return;
								}

								var newCodeStart = Memory.GetMethodStart(newMethod, out Exception ex2);
								if (ex2 != null)
								{
									Log.Warning($"Reloader: exception getting new method: {ex2.Message}");
									return;
								}
								Memory.WriteJump(originalCodeStart, newCodeStart);
								harmony.Patch(newMethod, null, null, transpiler);
							}
							else
								Log.Warning("Reloader: original missing");
						}
					})
				);
		}

		bool ExcludedDLLs(string path)
		{
			return path.EndsWith("0Harmony.dll") || path.EndsWith("0Reloader.dll");
		}

		public static BindingFlags allBindings =
			BindingFlags.Public
			| BindingFlags.NonPublic
			| BindingFlags.Instance
			| BindingFlags.Static
			| BindingFlags.GetField
			| BindingFlags.SetField
			| BindingFlags.GetProperty
			| BindingFlags.SetProperty;
	}
}