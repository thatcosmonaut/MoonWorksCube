﻿using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using MoonWorks;
using MoonWorks.Graphics;
using MoonWorks.Math;
using MoonWorks.Math.Float;

public class Program : Game
{
	public static void Main(string[] args)
	{
		Program p = new Program(
			new WindowCreateInfo(
				"Cube",
				640,
				480,
				ScreenMode.Windowed
			),
			PresentMode.Immediate,
			new FramerateSettings
			{
				Mode = FramerateMode.Capped,
				Cap = 144
			}
		);
		p.Run();
	}

	GraphicsPipeline cubePipeline;
	GraphicsPipeline skyboxPipeline;
	Texture depthTexture;
	Buffer cubeVertexBuffer;
	Buffer skyboxVertexBuffer;
	Buffer indexBuffer;
	Texture skyboxTexture;
	Sampler skyboxSampler;
	bool finishedLoading;

	float cubeTimer = 0f;
	Quaternion cubeRotation = Quaternion.Identity;
	Quaternion previousCubeRotation = Quaternion.Identity;

	Stopwatch timer;

	struct Uniforms
	{
		public Matrix4x4 ViewProjection;
	}

	[StructLayout(LayoutKind.Sequential)]
	struct PositionColorVertex
	{
		public Vector3 Position;
		public Color Color;

		public PositionColorVertex(Vector3 position, Color color)
		{
			Position = position;
			Color = color;
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	struct PositionVertex
	{
		public Vector3 Position;

		public PositionVertex(Vector3 position)
		{
			Position = position;
		}
	}

	void LoadCubemap(CommandBuffer cmdbuf, string[] imagePaths)
	{
		System.IntPtr textureData;
		int w, h, numChannels;

		for (uint i = 0; i < imagePaths.Length; i++)
		{
			textureData = RefreshCS.Refresh.Refresh_Image_Load(
				imagePaths[i],
				out w,
				out h,
				out numChannels
			);
			cmdbuf.SetTextureData(
				new TextureSlice(
					skyboxTexture,
					new Rect(0, 0, w, h),
					0,
					i
				),
				textureData,
				(uint) (w * h * 4) // w * h * numChannels does not work
			);
			RefreshCS.Refresh.Refresh_Image_Free(textureData);
		}
	}

	public Program(WindowCreateInfo windowCreateInfo, PresentMode presentMode, FramerateSettings framerateSettings)
		: base(windowCreateInfo, presentMode, framerateSettings, 15, true)
	{
		string baseContentPath = Path.Combine(
			System.AppDomain.CurrentDomain.BaseDirectory,
			"Content"
		);

		ShaderModule cubeVertShaderModule = new ShaderModule(
			GraphicsDevice,
			Path.Combine(baseContentPath, "cube_vert.spv")
		);
		ShaderModule cubeFragShaderModule = new ShaderModule(
			GraphicsDevice,
			Path.Combine(baseContentPath, "cube_frag.spv")
		);

		ShaderModule skyboxVertShaderModule = new ShaderModule(
			GraphicsDevice,
			Path.Combine(baseContentPath, "skybox_vert.spv")
		);
		ShaderModule skyboxFragShaderModule = new ShaderModule(
			GraphicsDevice,
			Path.Combine(baseContentPath, "skybox_frag.spv")
		);

		depthTexture = Texture.CreateTexture2D(
			GraphicsDevice,
			Window.Width,
			Window.Height,
			TextureFormat.D16,
			TextureUsageFlags.DepthStencilTarget
		);

		skyboxTexture = Texture.CreateTextureCube(
			GraphicsDevice,
			2048,
			TextureFormat.R8G8B8A8,
			TextureUsageFlags.Sampler
		);
		skyboxSampler = new Sampler(GraphicsDevice, new SamplerCreateInfo());

		cubeVertexBuffer = Buffer.Create<PositionColorVertex>(
			GraphicsDevice,
			BufferUsageFlags.Vertex,
			24
		);
		skyboxVertexBuffer = Buffer.Create<PositionVertex>(
			GraphicsDevice,
			BufferUsageFlags.Vertex,
			24
		);
		indexBuffer = Buffer.Create<ushort>(
			GraphicsDevice,
			BufferUsageFlags.Index,
			36
		);

		Task loadingTask = Task.Run(() => UploadGPUAssets(baseContentPath));

		cubePipeline = new GraphicsPipeline(
			GraphicsDevice,
			new GraphicsPipelineCreateInfo
			{
				AttachmentInfo = new GraphicsPipelineAttachmentInfo(
					TextureFormat.D16,
					new ColorAttachmentDescription(
						GraphicsDevice.GetSwapchainFormat(Window),
						ColorAttachmentBlendState.Opaque
					)
				),
				DepthStencilState = DepthStencilState.DepthReadWrite,
				VertexShaderInfo = GraphicsShaderInfo.Create<Uniforms>(cubeVertShaderModule, "main", 0),
				VertexInputState = new VertexInputState(
					VertexBinding.Create<PositionColorVertex>(),
					VertexAttribute.Create<PositionColorVertex>("Position", 0),
					VertexAttribute.Create<PositionColorVertex>("Color", 1)
				),
				PrimitiveType = PrimitiveType.TriangleList,
				FragmentShaderInfo = GraphicsShaderInfo.Create(cubeFragShaderModule, "main", 0),
				RasterizerState = RasterizerState.CW_CullBack,
				MultisampleState = MultisampleState.None
			}
		);

		skyboxPipeline = new GraphicsPipeline(
			GraphicsDevice,
			new GraphicsPipelineCreateInfo
			{
				AttachmentInfo = new GraphicsPipelineAttachmentInfo(
					TextureFormat.D16,
					new ColorAttachmentDescription(
						GraphicsDevice.GetSwapchainFormat(Window),
						ColorAttachmentBlendState.Opaque
					)
				),
				DepthStencilState = DepthStencilState.DepthReadWrite,
				VertexShaderInfo = GraphicsShaderInfo.Create<Uniforms>(skyboxVertShaderModule, "main", 0),
				VertexInputState = new VertexInputState(
					VertexBinding.Create<PositionVertex>(),
					VertexAttribute.Create<PositionVertex>("Position", 0)
				),
				PrimitiveType = PrimitiveType.TriangleList,
				FragmentShaderInfo = GraphicsShaderInfo.Create(skyboxFragShaderModule, "main", 1),
				RasterizerState = RasterizerState.CW_CullNone,
				MultisampleState = MultisampleState.None,
			}
		);

		timer = new Stopwatch();
		timer.Start();
	}

	private void UploadGPUAssets(string baseContentPath)
	{
		System.Console.WriteLine("Loading...");

		// Begin submitting resource data to the GPU.
		CommandBuffer cmdbuf = GraphicsDevice.AcquireCommandBuffer();

		cmdbuf.SetBufferData(
			cubeVertexBuffer,
			new PositionColorVertex[]
			{
				new PositionColorVertex(new Vector3(-1, -1, -1), new Color(1f, 0f, 0f)),
				new PositionColorVertex(new Vector3(1, -1, -1), new Color(1f, 0f, 0f)),
				new PositionColorVertex(new Vector3(1, 1, -1), new Color(1f, 0f, 0f)),
				new PositionColorVertex(new Vector3(-1, 1, -1), new Color(1f, 0f, 0f)),

				new PositionColorVertex(new Vector3(-1, -1, 1), new Color(0f, 1f, 0f)),
				new PositionColorVertex(new Vector3(1, -1, 1), new Color(0f, 1f, 0f)),
				new PositionColorVertex(new Vector3(1, 1, 1), new Color(0f, 1f, 0f)),
				new PositionColorVertex(new Vector3(-1, 1, 1), new Color(0f, 1f, 0f)),

				new PositionColorVertex(new Vector3(-1, -1, -1), new Color(0f, 0f, 1f)),
				new PositionColorVertex(new Vector3(-1, 1, -1), new Color(0f, 0f, 1f)),
				new PositionColorVertex(new Vector3(-1, 1, 1), new Color(0f, 0f, 1f)),
				new PositionColorVertex(new Vector3(-1, -1, 1), new Color(0f, 0f, 1f)),

				new PositionColorVertex(new Vector3(1, -1, -1), new Color(1f, 0.5f, 0f)),
				new PositionColorVertex(new Vector3(1, 1, -1), new Color(1f, 0.5f, 0f)),
				new PositionColorVertex(new Vector3(1, 1, 1), new Color(1f, 0.5f, 0f)),
				new PositionColorVertex(new Vector3(1, -1, 1), new Color(1f, 0.5f, 0f)),

				new PositionColorVertex(new Vector3(-1, -1, -1), new Color(1f, 0f, 0.5f)),
				new PositionColorVertex(new Vector3(-1, -1, 1), new Color(1f, 0f, 0.5f)),
				new PositionColorVertex(new Vector3(1, -1, 1), new Color(1f, 0f, 0.5f)),
				new PositionColorVertex(new Vector3(1, -1, -1), new Color(1f, 0f, 0.5f)),

				new PositionColorVertex(new Vector3(-1, 1, -1), new Color(0f, 0.5f, 0f)),
				new PositionColorVertex(new Vector3(-1, 1, 1), new Color(0f, 0.5f, 0f)),
				new PositionColorVertex(new Vector3(1, 1, 1), new Color(0f, 0.5f, 0f)),
				new PositionColorVertex(new Vector3(1, 1, -1), new Color(0f, 0.5f, 0f))
			}
		);

		cmdbuf.SetBufferData(
			skyboxVertexBuffer,
			new PositionVertex[]
			{
				new PositionVertex(new Vector3(-10, -10, -10)),
				new PositionVertex(new Vector3(10, -10, -10)),
				new PositionVertex(new Vector3(10, 10, -10)),
				new PositionVertex(new Vector3(-10, 10, -10)),

				new PositionVertex(new Vector3(-10, -10, 10)),
				new PositionVertex(new Vector3(10, -10, 10)),
				new PositionVertex(new Vector3(10, 10, 10)),
				new PositionVertex(new Vector3(-10, 10, 10)),

				new PositionVertex(new Vector3(-10, -10, -10)),
				new PositionVertex(new Vector3(-10, 10, -10)),
				new PositionVertex(new Vector3(-10, 10, 10)),
				new PositionVertex(new Vector3(-10, -10, 10)),

				new PositionVertex(new Vector3(10, -10, -10)),
				new PositionVertex(new Vector3(10, 10, -10)),
				new PositionVertex(new Vector3(10, 10, 10)),
				new PositionVertex(new Vector3(10, -10, 10)),

				new PositionVertex(new Vector3(-10, -10, -10)),
				new PositionVertex(new Vector3(-10, -10, 10)),
				new PositionVertex(new Vector3(10, -10, 10)),
				new PositionVertex(new Vector3(10, -10, -10)),

				new PositionVertex(new Vector3(-10, 10, -10)),
				new PositionVertex(new Vector3(-10, 10, 10)),
				new PositionVertex(new Vector3(10, 10, 10)),
				new PositionVertex(new Vector3(10, 10, -10))
			}
		);

		cmdbuf.SetBufferData(
			indexBuffer,
			new ushort[]
			{
				0, 1, 2,	0, 2, 3,
				6, 5, 4,	7, 6, 4,
				8, 9, 10,	8, 10, 11,
				14, 13, 12,	15, 14, 12,
				16, 17, 18,	16, 18, 19,
				22, 21, 20,	23, 22, 20
			}
		);

		LoadCubemap(cmdbuf, new string[]
		{
			Path.Combine(baseContentPath, "right.png"),
			Path.Combine(baseContentPath, "left.png"),
			Path.Combine(baseContentPath, "top.png"),
			Path.Combine(baseContentPath, "bottom.png"),
			Path.Combine(baseContentPath, "front.png"),
			Path.Combine(baseContentPath, "back.png"),
		});

		GraphicsDevice.Submit(cmdbuf);

		finishedLoading = true;
		System.Console.WriteLine("Finished loading!");
	}

	protected override void Update(System.TimeSpan dt)
	{
		cubeTimer += (float) dt.TotalSeconds;

		previousCubeRotation = cubeRotation;

		cubeRotation = Quaternion.CreateFromYawPitchRoll(
			cubeTimer * 2f,
			0,
			cubeTimer * 2f
		);
	}

	protected override void Draw(double alpha)
	{
		Matrix4x4 proj = Matrix4x4.CreatePerspectiveFieldOfView(MathHelper.ToRadians(75f), (float)Window.Width / Window.Height, 0.01f, 100f);
		Matrix4x4 view = Matrix4x4.CreateLookAt(new Vector3(0, 1.5f, 4f), Vector3.Zero, Vector3.Up);
		Uniforms skyboxUniforms = new Uniforms { ViewProjection = view * proj };

		Matrix4x4 model = Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(previousCubeRotation, cubeRotation, (float) alpha));
		Matrix4x4 cubeModelViewProjection = model * view * proj;
		Uniforms cubeUniforms = new Uniforms { ViewProjection = cubeModelViewProjection };

		CommandBuffer cmdbuf = GraphicsDevice.AcquireCommandBuffer();

		Texture? swapchainTexture = cmdbuf.AcquireSwapchainTexture(Window);

		if (swapchainTexture != null)
		{
			if (!finishedLoading)
			{
				float sine = (float) System.Math.Abs(System.Math.Sin(timer.Elapsed.TotalSeconds));
				Color clearColor = new Color(sine, sine, sine);

				// Just show a clear screen.
				cmdbuf.BeginRenderPass(
					new DepthStencilAttachmentInfo(depthTexture, new DepthStencilValue(1f, 0)),
					new ColorAttachmentInfo(swapchainTexture, clearColor)
				);
				cmdbuf.EndRenderPass();
			}
			else
			{
				cmdbuf.BeginRenderPass(
					new DepthStencilAttachmentInfo(depthTexture, new DepthStencilValue(1f, 0)),
					new ColorAttachmentInfo(swapchainTexture, Color.CornflowerBlue)
				);

				// Draw cube
				cmdbuf.BindGraphicsPipeline(cubePipeline);
				cmdbuf.BindVertexBuffers(cubeVertexBuffer);
				cmdbuf.BindIndexBuffer(indexBuffer, IndexElementSize.Sixteen);
				uint vertexParamOffset = cmdbuf.PushVertexShaderUniforms(cubeUniforms);
				cmdbuf.DrawIndexedPrimitives(0, 0, 12, vertexParamOffset, 0);

				// Draw skybox
				cmdbuf.BindGraphicsPipeline(skyboxPipeline);
				cmdbuf.BindVertexBuffers(skyboxVertexBuffer);
				cmdbuf.BindIndexBuffer(indexBuffer, IndexElementSize.Sixteen);
				cmdbuf.BindFragmentSamplers(new TextureSamplerBinding(skyboxTexture, skyboxSampler));
				vertexParamOffset = cmdbuf.PushVertexShaderUniforms(skyboxUniforms);
				cmdbuf.DrawIndexedPrimitives(0, 0, 12, vertexParamOffset, 0);

				cmdbuf.EndRenderPass();
			}
		}

		GraphicsDevice.Submit(cmdbuf);
	}
}
