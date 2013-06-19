using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace Qisope
{
	public class FlashProxyFactory : IFlashProxyFactory
	{
		private const string FLASHCONNECTOR_FIELD_NAME = "FlashConnector";
		private const string RUNTIME_MODULE_BUILDER_NAME = "FlashConnector_RuntimeModuleBuilder";
		private const string RUNTIME_TYPE_ASSEMBLY_BUILDER_NAME = "FlashConnector_RuntimeTypeAssemblyBuilder";

		private readonly ModuleBuilder _runtimeModuleBuilder;
		private readonly AssemblyBuilder _runtimeTypeAssembly;
		private readonly Dictionary<Type, Type> _typeCache;
		private readonly Dictionary<Type, MethodInfo[]> _methodInfoCache;

		public FlashProxyFactory()
		{
			_typeCache = new Dictionary<Type, Type>();
			_methodInfoCache = new Dictionary<Type, MethodInfo[]>();

			var assemblyName = new AssemblyName(RUNTIME_TYPE_ASSEMBLY_BUILDER_NAME);

			_runtimeTypeAssembly = AppDomain.CurrentDomain.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
			_runtimeModuleBuilder = _runtimeTypeAssembly.DefineDynamicModule(RUNTIME_MODULE_BUILDER_NAME);
		}

		public TProxyType CreateProxy<TProxyType>(FlashWrapper flashWrapper, string flashSessionID) where TProxyType : FlashInterfaceBase
		{
			Type targetType = typeof(TProxyType);
			return (TProxyType)CreateProxy(targetType, flashWrapper, flashSessionID);
		}

		public FlashInterfaceBase CreateProxy(Type targetType, FlashWrapper flashWrapper, string flashSessionID)
		{
			Type emittedProxyType;
			MethodInfo[] proxyMethodInfos;

			IFlashConnector flashConnector = CreateFlashConnector(flashWrapper, flashSessionID);

			if (!_typeCache.TryGetValue(targetType, out emittedProxyType))
			{
				string proxyTypeName = string.Format("{0}Proxy", targetType.Name);
				TypeBuilder typeBuilder = _runtimeModuleBuilder.DefineType(proxyTypeName, TypeAttributes.Public, targetType);
				FieldBuilder flashConnectorField = typeBuilder.DefineField(FLASHCONNECTOR_FIELD_NAME, typeof (IFlashConnector), FieldAttributes.InitOnly | FieldAttributes.Private);
				proxyMethodInfos = targetType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic);

				ImplementProxyConstructor(targetType, typeBuilder, flashConnectorField);
				ImplementProxyMethods(proxyMethodInfos, typeBuilder, flashConnectorField);

				emittedProxyType = typeBuilder.CreateType();

				_typeCache.Add(targetType, emittedProxyType);
				_methodInfoCache.Add(targetType, proxyMethodInfos);
			}
			else
			{
				proxyMethodInfos = _methodInfoCache[targetType];
			}

			FlashInterfaceBase proxy = (FlashInterfaceBase)Activator.CreateInstance(emittedProxyType, flashConnector);
			flashConnector.RegisterFlashInterface(proxy);
			RegisterCallbackMethods(proxyMethodInfos, flashConnector);
			return proxy;
		}

		private static void ImplementProxyMethods(IEnumerable<MethodInfo> proxyMethodInfos, TypeBuilder typeBuilder, FieldInfo flashConnectorField)
		{
			MethodInfo invokeFlashMethod = typeof (IFlashConnector).GetMethod(FlashConnector.INVOKE_FLASH_METHOD_NAME);

			foreach (MethodInfo methodInfo in proxyMethodInfos)
			{
				if (methodInfo.IsAbstract)
				{
					FlashMethodAttribute flashMethodAttribute = GetMethodAttribute<FlashMethodAttribute>(methodInfo);

					if (flashMethodAttribute != null)
					{
						ImplementProxyMethod(methodInfo, typeBuilder, flashConnectorField, flashMethodAttribute, invokeFlashMethod);
					}
				}
			}
		}

		private static void ImplementProxyMethod(MethodInfo targetMethodInfo, TypeBuilder typeBuilder, FieldInfo flashConnectorField, FlashMethodAttribute flashMethodAttribute, MethodInfo invokeFlashMethod)
		{
			Type[] parameterTypes = Array.ConvertAll(targetMethodInfo.GetParameters(), input => input.ParameterType);
			string methodBodyName = string.Format("{0}Body", targetMethodInfo.Name);
			MethodBuilder methodBuilder = typeBuilder.DefineMethod(methodBodyName, MethodAttributes.Private | MethodAttributes.HideBySig | MethodAttributes.Virtual | MethodAttributes.Final, targetMethodInfo.ReturnType, parameterTypes);

			string flashMethodName = !string.IsNullOrEmpty(flashMethodAttribute.FlashMethodName) ? flashMethodAttribute.FlashMethodName : targetMethodInfo.Name;
			string methodReturnType = targetMethodInfo.ReturnType.FullName;

			ILGenerator generator = methodBuilder.GetILGenerator();
			LocalBuilder argsArrayBuilder = generator.DeclareLocal(typeof (object[]));

			// Store the flash connector on the stack - this is the target of our eventual method call
			generator.Emit(OpCodes.Ldarg_0);
			generator.Emit(OpCodes.Ldfld, flashConnectorField);

			// Store the name of the flash method to call on the stack
			generator.Emit(OpCodes.Ldstr, flashMethodName);

			// Store the full name of the .NET return type on the stack
			generator.Emit(OpCodes.Ldstr, methodReturnType);

			// Create the parameter array - we are calling a method with a (params object[]) signature
			generator.Emit(OpCodes.Ldc_I4, parameterTypes.Length);
			generator.Emit(OpCodes.Newarr, typeof (object));
			generator.Emit(OpCodes.Stloc_S, argsArrayBuilder);

			// Add the parameters to the parameter array
			for (int i = 0; i < parameterTypes.Length; i++)
			{
				generator.Emit(OpCodes.Ldloc_S, argsArrayBuilder.LocalIndex);
				generator.Emit(OpCodes.Ldc_I4, i);
				generator.Emit(OpCodes.Ldarg_S, i + 1);
				generator.Emit(OpCodes.Box, parameterTypes[i]);
				generator.Emit(OpCodes.Stelem_Ref);
			}

			// Push the parameter array onto the stack and call the flash connector method which will make the call into flash
			generator.Emit(OpCodes.Ldloc_S, argsArrayBuilder.LocalIndex);
			generator.Emit(OpCodes.Callvirt, invokeFlashMethod);

			// If we don't want a return value - get rid of it
			if (targetMethodInfo.ReturnType == typeof (void))
			{
				generator.Emit(OpCodes.Pop);
			}
			else if (targetMethodInfo.ReturnType.IsPrimitive)
			{
				generator.Emit(OpCodes.Unbox_Any, targetMethodInfo.ReturnType);
			}

			generator.Emit(OpCodes.Ret);
			typeBuilder.DefineMethodOverride(methodBuilder, targetMethodInfo);
		}

		private static void RegisterCallbackMethods(IEnumerable<MethodInfo> proxyMethodInfos, IFlashConnector flashConnector)
		{
			foreach (MethodInfo methodInfo in proxyMethodInfos)
			{
				FlashCallbackAttribute flashCallbackAttribute = GetMethodAttribute<FlashCallbackAttribute>(methodInfo);

				if (flashCallbackAttribute != null)
				{
					string callbackMethodName = !string.IsNullOrEmpty(flashCallbackAttribute.CallbackMethodName) ? flashCallbackAttribute.CallbackMethodName : methodInfo.Name;
					flashConnector.AddCallback(callbackMethodName, methodInfo);
				}
			}
		}

		private static void ImplementProxyConstructor(Type targetType, TypeBuilder typeBuilder, FieldInfo flashConnectorField)
		{
			ConstructorBuilder constructorBuilder = typeBuilder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new[] {typeof (IFlashConnector)});
			ILGenerator g = constructorBuilder.GetILGenerator();
			
			// invoke the object constructor
			g.Emit(OpCodes.Ldarg_0);
			ConstructorInfo constructor = targetType.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, CallingConventions.Any, Type.EmptyTypes, null) ?? typeof(object).GetConstructor(Type.EmptyTypes);
			g.Emit(OpCodes.Call, constructor);
			
			
			// This constructor takes a single parameter of typeo IFlashConnector - store this value in the class field
			g.Emit(OpCodes.Ldarg_0);
			g.Emit(OpCodes.Ldarg_1);
			g.Emit(OpCodes.Stfld, flashConnectorField);
			g.Emit(OpCodes.Ret);
		}

		private static TAttribute GetMethodAttribute<TAttribute>(ICustomAttributeProvider methodInfo) where TAttribute : class
		{
			object[] customAttributes = methodInfo.GetCustomAttributes(typeof (TAttribute), true);
			return customAttributes.Length == 0 ? null : (TAttribute)customAttributes[0];
		}

		internal virtual IFlashConnector CreateFlashConnector(FlashWrapper flashWrapper, string flashSessionID)
		{
			return new FlashConnector(flashWrapper, flashSessionID);
		}

		public static void Unregister(FlashInterfaceBase flashInterface)
		{
			Type type = flashInterface.GetType();
			FieldInfo field = type.GetField(FLASHCONNECTOR_FIELD_NAME, BindingFlags.NonPublic | BindingFlags.Instance);
			FlashConnector flashConnector = (FlashConnector)field.GetValue(flashInterface);
			flashConnector.Unregister();
		}
	}
}