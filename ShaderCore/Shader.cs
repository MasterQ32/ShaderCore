﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ShaderCore;
using NLua;
using System.Collections.Specialized;
using OpenTK.Graphics.OpenGL4;

namespace ShaderCore
{
	/// <summary>
	/// Defines an abstract shader.
	/// </summary>
	public class Shader
	{
		private class ShaderPart
		{
			public string type;
			public string className;
			public string source;
			public NameValueCollection properties = new NameValueCollection();
		}

		private class LuaShader
		{
			private Shader shader;
			internal LuaShader(Shader shader)
			{
				this.shader = shader;
			}

			public string version { get { return shader.version; } set { shader.version = value; } }

			public void add(LuaTable table)
			{
				var sp = new ShaderPart();
				sp.type = table["type"] as string;
				sp.className = table["class"] as string;
				sp.source = table["source"] as string;

				if (sp.type == null)
					throw new InvalidShaderException("Shader is missing a type declaration");
				if (sp.source == null)
					throw new InvalidShaderException("Shader is missing a source declaration");

				foreach (string k in table.Keys)
				{
					if (k == "type" || k == "class" || k == "source")
						continue;
					sp.properties[k] = table[k] as string;
				}
				shader.parts.Add(sp);
			}

			public void addDefault(string name)
			{
				var sp = new ShaderPart();
				if (!this.shader.Manager.DefaultShaders.ContainsKey(name))
				{
					throw new InvalidOperationException("Failed to add default shader: " + name + " is not a valid default shader!");
				}

				var defaultShader = this.shader.Manager.DefaultShaders[name];

				foreach (var include in defaultShader.Includes)
				{
					this.include(include);
				}
				sp.type = defaultShader.ShaderType;
				sp.source = defaultShader.Source;
				shader.parts.Add(sp);
			}

			public void include(string name)
			{
				if (shader.includes.ContainsKey(name))
					return;

				if (!this.shader.manager.IncludeFiles.ContainsKey(name))
				{
					throw new InvalidOperationException("include file '" + name + "' not found.");
				}

				shader.includes.Add(name, this.shader.manager.IncludeFiles[name]);
			}
		}

		private readonly ShaderContext manager;
		private string version = "400 core";
		private string source;
		private ShaderCache cache;
		private List<ShaderPart> parts;
		private Dictionary<string, string> includes;

		/// <summary>
		/// Creates a new shader.
		/// </summary>
		internal Shader(ShaderContext manager)
		{
			this.manager = manager;
			this.parts = new List<ShaderPart>();
			this.includes = new Dictionary<string, string>();
			this.cache = new ShaderCache(this);
			this.cache.Miss += cache_Miss;
			this.Variables = this;
		}

		/// <summary>
		/// Loads the shader source code for this 
		/// </summary>
		/// <param name="source">Source code of the shader.</param>
		/// <param name="tag">The tag is provided as a lua variable named tag.</param>
		public void Load(string source, object tag = null)
		{
			// Copy the source to the internal source.
			this.source = source;

			// Clear the shader cache to force recompilation.
			this.cache.Clear();
			this.includes.Clear();

			// Analyze and load shader source
			var ls = new LuaShader(this);
			this.parts = new List<ShaderPart>();

			if (!string.IsNullOrWhiteSpace(source))
			{
				// Temporary lua state because shader source is lua code
				using (Lua lua = new Lua())
				{
					// Provide global variable
					lua["shader"] = ls;
					lua["tag"] = tag ?? new object();
					lua.DoString(source, "shader-code");
				}
			}
		}

		/// <summary>
		/// Selects the default shader program from this shader.
		/// </summary>
		/// <returns></returns>
		public CompiledShader Select()
		{
			return this.Select(null, new ShaderFragment[0]);
		}

		/// <summary>
		/// Selects the default shader program with overwritten shader fragments from this shader.
		/// </summary>
		/// <param name="fragments">Custom shader fragments that are used instead of the original shader.</param>
		/// <returns></returns>
		public CompiledShader Select(params ShaderFragment[] fragments)
		{
			return this.Select(null, fragments);
		}

		/// <summary>
		/// Selects a shader program with a specific class from this shader.
		/// </summary>
		/// <param name="className">Shader class to select.</param>
		/// <returns></returns>
		public CompiledShader Select(string className)
		{
			return this.Select(className, new ShaderFragment[0]);
		}

		/// <summary>
		/// Selects a shader program with a specific class with overwritten shader fragments from this shader.
		/// </summary>
		/// <param name="className">Shader class to select.</param>
		/// <param name="fragments">Custom shader fragments that are used instead of the original shader.</param>
		/// <returns></returns>
		public CompiledShader Select(string className, params ShaderFragment[] fragments)
		{
			return this.cache.Select(className, fragments);
		}

		void cache_Miss(object sender, ShaderCacheMissEventArgs e)
		{
			if (e.ResultShader != null) return;

			ShaderFragment vertexShader = e.Fragments.FirstOrDefault((s) => s.Type == ShaderType.VertexShader);
			ShaderFragment tessCtrlShader = e.Fragments.FirstOrDefault((s) => s.Type == ShaderType.TessControlShader);
			ShaderFragment tessEvalShader = e.Fragments.FirstOrDefault((s) => s.Type == ShaderType.TessEvaluationShader);
			ShaderFragment geometryShader = e.Fragments.FirstOrDefault((s) => s.Type == ShaderType.GeometryShader);
			ShaderFragment fragmentShader = e.Fragments.FirstOrDefault((s) => s.Type == ShaderType.FragmentShader);

			string header = "#version " + this.version + "\n";
			foreach (var include in this.includes)
			{
				header += include.Value + "\n";
			}
			foreach (var sp in this.parts)
			{
				if (sp.type == "global" && (sp.className == null || sp.className == e.ShaderClass))
					header += sp.source + "\n";
			}

			CompileIfNecessary(ref vertexShader, header, e.ShaderClass, ShaderType.VertexShader);
			CompileIfNecessary(ref tessCtrlShader, header, e.ShaderClass, ShaderType.TessControlShader);
			CompileIfNecessary(ref tessEvalShader, header, e.ShaderClass, ShaderType.TessEvaluationShader);
			CompileIfNecessary(ref geometryShader, header, e.ShaderClass, ShaderType.GeometryShader);
			CompileIfNecessary(ref fragmentShader, header, e.ShaderClass, ShaderType.FragmentShader);

			int program = GL.CreateProgram();
			if (vertexShader != null)
				GL.AttachShader(program, vertexShader.Id);
			if (tessCtrlShader != null)
				GL.AttachShader(program, tessCtrlShader.Id);
			if (tessEvalShader != null)
				GL.AttachShader(program, tessEvalShader.Id);
			if (geometryShader != null)
				GL.AttachShader(program, geometryShader.Id);
			if (fragmentShader != null)
				GL.AttachShader(program, fragmentShader.Id);

			GL.LinkProgram(program);

			int result;
			GL.GetProgram(program, GetProgramParameterName.LinkStatus, out result);
			string infoLog = GL.GetProgramInfoLog(program);

			if (result == 0)
			{
				this.manager.Log(LocalizedStrings.Default.ShaderLinkerFailed);
				if (!string.IsNullOrWhiteSpace(infoLog))
					this.manager.Log(infoLog);
				GL.DeleteProgram(program);
				return;
			}
			else
			{
				if (!string.IsNullOrWhiteSpace(infoLog))
				{
					this.manager.Log(LocalizedStrings.Default.ShaderLinkerResult);
					this.manager.Log(infoLog);
				}
			}

			e.ResultShader = new CompiledShader(this, program, tessCtrlShader != null);
		}

		private void CompileIfNecessary(ref ShaderFragment fragment, string header, string @class, ShaderType type)
		{
			// No compilation needed
			if (fragment != null) return;

			string typeName = GetShaderName(type);

			ShaderPart source = null;
			foreach (var sp in this.parts)
			{
				if (sp.type != typeName)
					continue;
				if (sp.className != @class)
					continue;
				source = sp;
				break;
			}
			if (source == null)
			{
				// Search for default shader
				foreach (var sp in this.parts)
				{
					if (sp.type != typeName)
						continue;
					if (sp.className != null)
						continue;
					source = sp;
					break;
				}
			}

			if (source == null)
				return;	// No source found, no shader available

			string input = "";
			input = GetInput(source.properties["input"]);

			fragment = new ShaderFragment(this, type, header + input + source.source);
		}

		private string GetInput(string input)
		{
			if (input == null) return "";
			if (!this.manager.InputLayouts.ContainsKey(input))
			{
				throw new NotSupportedException(input + " is not a supported input type!");
			}
			return this.manager.InputLayouts[input];
		}

		private string GetShaderName(ShaderType type)
		{
			switch (type)
			{
				case ShaderType.VertexShader:
					return "vertex";
				case ShaderType.TessControlShader:
					return "tess-control";
				case ShaderType.TessEvaluationShader:
					return "tess-eval";
				case ShaderType.GeometryShader:
					return "geometry";
				case ShaderType.FragmentShader:
					return "fragment";
				default:
					throw new NotSupportedException();
			}
		}

		/// <summary>
		/// Gets or sets the shader variables.
		/// </summary>
		/// <remarks>These variables will be passed to all compiled shaders as uniforms.</remarks>
		public object Variables { get; set; }

		/// <summary>
		/// Gets the shader manager this shader was created with.
		/// </summary>
		/// <returns></returns>
		public ShaderContext Manager
		{
			get
			{
				return this.manager;
			}
		}
	}

	/// <summary>
	/// Defines an abstract shader that has custom typed variables
	/// </summary>
	/// <typeparam name="T"></typeparam>
	public class Shader<T> : Shader
		where T : class
	{
		/// <summary>
		/// Creates a new shader.
		/// </summary>
		public Shader(ShaderContext manager)
			: base(manager)
		{
			// Reset variables to null, so we don't get inconsistence.
			this.Variables = null;
		}

		/// <summary>
		/// Gets or sets the shader variables.
		/// </summary>
		/// <remarks>These variables will be passed to all compiled shaders as uniforms.</remarks>
		public new T Variables
		{
			get
			{
				return base.Variables as T;
			}
			set
			{
				base.Variables = value;
			}
		}
	}
}
