// ObjectValueTree.cs
//
// Author:
//   Lluis Sanchez Gual <lluis@novell.com>
//
// Copyright (c) 2008 Novell, Inc (http://www.novell.com)
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//
//

using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

using Gtk;
using Mono.Debugging.Client;
using MonoDevelop.Components;
using MonoDevelop.Core;
using MonoDevelop.Ide;
using MonoDevelop.Ide.CodeCompletion;
using MonoDevelop.Components.Commands;
using MonoDevelop.Ide.Commands;
using Mono.TextEditor;


namespace MonoDevelop.Debugger
{
	[System.ComponentModel.ToolboxItem (true)]
	public class ObjectValueTreeView : TreeView, ICompletionWidget
	{
		readonly Dictionary<ObjectValue, TreeRowReference> nodes = new Dictionary<ObjectValue, TreeRowReference> ();
		readonly Dictionary<string, ObjectValue> cachedValues = new Dictionary<string, ObjectValue> ();
		readonly Dictionary<ObjectValue, Task> expandTasks = new Dictionary<ObjectValue, Task> ();
		readonly CancellationTokenSource cancellationTokenSource = new CancellationTokenSource ();
		readonly Dictionary<string, string> oldValues = new Dictionary<string, string> ();
		readonly List<ObjectValue> values = new List<ObjectValue> ();
		readonly List<string> valueNames = new List<string> ();

		readonly Xwt.Drawing.Image noLiveIcon;
		readonly Xwt.Drawing.Image liveIcon;

		readonly TreeViewState state;
		readonly TreeStore store;
		readonly string createMsg;
		bool restoringState;
		bool compact;
		StackFrame frame;
		bool disposed;
		
		bool columnsAdjusted;
		bool columnSizesUpdating;
		bool allowStoreColumnSizes;
		double expColWidth;
		double valueColWidth;
		double typeColWidth;

		readonly CellRendererTextWithIcon crtExp;
		readonly CellRendererText crtValue;
		readonly CellRendererText crtType;
		readonly CellRendererRoundedButton crpButton;
		readonly CellRendererImage crpPin;
		readonly CellRendererImage crpLiveUpdate;
		readonly CellRendererImage crpViewer;
		Entry editEntry;
		Mono.Debugging.Client.CompletionData currentCompletionData;
		
		readonly TreeViewColumn expCol;
		readonly TreeViewColumn valueCol;
		readonly TreeViewColumn typeCol;
		readonly TreeViewColumn pinCol;
		
		const string errorColor = "red";
		const string modifiedColor = "blue";
		const string disabledColor = "gray";
		
		static readonly CommandEntrySet menuSet;
		
		const int NameColumn = 0;
		const int ValueColumn = 1;
		const int TypeColumn = 2;
		public const int ObjectColumn = 3;
		const int NameEditableColumn = 4;
		const int ValueEditableColumn = 5;
		const int IconColumn = 6;
		const int NameColorColumn = 7;
		const int ValueColorColumn = 8;
		const int ValueButtonVisibleColumn = 9;
		const int PinIconColumn = 10;
		const int LiveUpdateIconColumn = 11;
		const int ViewerButtonVisibleColumn = 12;
		const int ColorPreviewColumn = 13;
		const int PreviewIconColumn = 14;
		const int EvaluateStatusIconColumn = 15;
		const int EvaluateStatusIconVisibleColumn = 16;
		const int ValueButtonTextColumn = 17;
		
		public event EventHandler StartEditing;
		public event EventHandler EndEditing;
		public event EventHandler PinStatusChanged;

		enum LocalCommands
		{
			AddWatch
		}
		
		static ObjectValueTreeView ()
		{
			// Context menu definition
			
			menuSet = new CommandEntrySet ();
			menuSet.AddItem (DebugCommands.AddWatch);
			menuSet.AddSeparator ();
			menuSet.AddItem (EditCommands.Copy);
			menuSet.AddItem (EditCommands.Rename);
			menuSet.AddItem (EditCommands.DeleteKey);
		}

		class CellRendererTextWithIcon : CellRendererText{

			IconId icon;

			[GLib.Property ("icon")]
			public string Icon {
				get {
					return icon;
				}
				set {
					icon = value;
				}
			}

			Xwt.Drawing.Image img {
				get {
					return ImageService.GetIcon (icon, IconSize.Menu);
				}
			}

			public override void GetSize (Widget widget, ref Gdk.Rectangle cell_area, out int x_offset, out int y_offset, out int width, out int height)
			{
				base.GetSize (widget, ref cell_area, out x_offset, out y_offset, out width, out height);
				if (!icon.IsNull)
					width += (int)(Xpad * 2 + img.Width);
			}

			protected override void Render (Gdk.Drawable window, Widget widget, Gdk.Rectangle background_area, Gdk.Rectangle cell_area, Gdk.Rectangle expose_area, CellRendererState flags)
			{
				base.Render (window, widget, background_area, cell_area, expose_area, flags);
				if (!icon.IsNull) {
					using (var ctx = Gdk.CairoHelper.Create (window)) {
						var layout = new Pango.Layout (widget.PangoContext);
						layout.FontDescription = FontDesc.Copy ();
						layout.FontDescription.Family = Family;
						layout.SetText (Text);
						int w, h;
						layout.GetPixelSize (out w, out h);
						var x = cell_area.X + w + 3 * Xpad;
						var y = cell_area.Y + cell_area.Height / 2 - (int)(img.Height / 2);
						ctx.DrawImage (widget, img, x, y);
					}
				}
			}
		}

		class CellRendererTextUrl : CellRendererText{
			[GLib.Property ("texturl")]
			public string TextUrl {
				get {
					return Text;
				}
				set {
					Uri uri;
					if (value != null && Uri.TryCreate (value.Trim ('"', '{', '}'), UriKind.Absolute, out uri) && (uri.Scheme == "http" || uri.Scheme == "https")) {
						Underline = Pango.Underline.Single;
						Foreground = "#197CEF";
					} else {
						Underline = Pango.Underline.None;
					}
					Text = value;
				}
			}
		}

		class CellRendererColorPreview : CellRenderer
		{
			protected override void Render (Gdk.Drawable window, Widget widget, Gdk.Rectangle background_area, Gdk.Rectangle cell_area, Gdk.Rectangle expose_area, CellRendererState flags)
			{
				using (Cairo.Context cr = Gdk.CairoHelper.Create (window)) {
					cr.LineWidth = 0.5;
					double center_x = cell_area.X + Math.Round ((double)(cell_area.Width / 2d));
					double center_y = cell_area.Y + Math.Round ((double)(cell_area.Height / 2d));
					cr.Arc (center_x, center_y, 5, 0, 2 * Math.PI);
					cr.SetSourceRGBA (Color.Red, Color.Green, Color.Blue, 1);
					cr.FillPreserve ();
					cr.SetSourceRGBA (0, 0, 0, 1);
					cr.Stroke ();
				}
			}

			public override void GetSize (Widget widget, ref Gdk.Rectangle cell_area, out int x_offset, out int y_offset, out int width, out int height)
			{
				x_offset = y_offset = 0;
				height = width = 16;
			}

			public Xwt.Drawing.Color Color { get; set; }
		}

		class CellRendererRoundedButton : CellRenderer {
			private string text;
			[GLib.Property ("text")]
			public virtual string Text {
				get {
					return text;
				}
				set {
					text = value;
				}
			}

			const int TopBottomPadding = 2;

			protected override void Render (Gdk.Drawable window, Widget widget, Gdk.Rectangle background_area, Gdk.Rectangle cell_area, Gdk.Rectangle expose_area, CellRendererState flags)
			{
				if (string.IsNullOrEmpty (text)) {
					return;
				}
				using (var cr = Gdk.CairoHelper.Create (window)) {
					cr.RoundedRectangle (
						cell_area.X,
						cell_area.Y + TopBottomPadding,
						cell_area.Width,
						cell_area.Height - TopBottomPadding * 2,
						(cell_area.Height - (TopBottomPadding * 2)) / 2);
					cr.LineWidth = 1;
					cr.SetSourceRGB (233 / 255.0, 242 / 255.0, 252 / 255.0);
					cr.FillPreserve ();
					cr.SetSourceRGB (82 / 255.0, 148 / 255.0, 235 / 255.0);
					cr.Stroke ();
					var textSize = cr.TextExtents (text);
					cr.MoveTo (cell_area.X + (cell_area.Width - textSize.Width) / 2.0 + (textSize.XBearing * -1), cell_area.Y + cell_area.Height - (cell_area.Height - textSize.Height) / 2.0);
					cr.ShowText (text);
				}
			}

			public override void GetSize (Widget widget, ref Gdk.Rectangle cell_area, out int x_offset, out int y_offset, out int width, out int height)
			{
				x_offset = y_offset = 0;
				if (string.IsNullOrEmpty (text)) {
					width = 0;
					height = 0;
					return;
				}
				using (var cr = Gdk.CairoHelper.Create (widget.ParentWindow)) {
					height = cell_area.Height;
					var textSize = cr.TextExtents (text);
					width = (int)textSize.Width + (int)textSize.Height * 2;
				}
			}
		}

		public ObjectValueTreeView ()
		{
			store = new TreeStore (typeof(string), typeof(string), typeof(string), typeof(ObjectValue), typeof(bool), typeof(bool), typeof(string), typeof(string), typeof(string), typeof(bool), typeof(string), typeof(Xwt.Drawing.Image), typeof(bool), typeof(Xwt.Drawing.Color?), typeof(string), typeof(Xwt.Drawing.Image), typeof(bool), typeof(string));
			Model = store;
			RulesHint = true;
			EnableSearch = false;
			AllowPopupMenu = true;
			Selection.Mode = Gtk.SelectionMode.Multiple;
			ResetColumnSizes ();
			
			Pango.FontDescription newFont = Style.FontDescription.Copy ();
			newFont.Size = (newFont.Size * 8) / 10;

			liveIcon = ImageService.GetIcon (Stock.Execute, IconSize.Menu);
			noLiveIcon = liveIcon.WithAlpha (0.5);
			
			expCol = new TreeViewColumn ();
			expCol.Title = GettextCatalog.GetString ("Name");
			var crp = new CellRendererImage ();
			expCol.PackStart (crp, false);
			expCol.AddAttribute (crp, "stock_id", IconColumn);
			crtExp = new CellRendererTextWithIcon ();
			expCol.PackStart (crtExp, true);
			expCol.AddAttribute (crtExp, "text", NameColumn);
			expCol.AddAttribute (crtExp, "editable", NameEditableColumn);
			expCol.AddAttribute (crtExp, "foreground", NameColorColumn);
			expCol.AddAttribute (crtExp, "icon", PreviewIconColumn);
			expCol.Resizable = true;
			expCol.Sizing = TreeViewColumnSizing.Fixed;
			expCol.MinWidth = 15;
			expCol.AddNotification ("width", OnColumnWidthChanged);
			AppendColumn (expCol);
			
			valueCol = new TreeViewColumn ();
			valueCol.Title = GettextCatalog.GetString ("Value");
			var evaluateStatusCell = new CellRendererImage ();
			valueCol.PackStart (evaluateStatusCell, false);
			valueCol.AddAttribute (evaluateStatusCell, "visible", EvaluateStatusIconVisibleColumn);
			valueCol.AddAttribute (evaluateStatusCell, "image", EvaluateStatusIconColumn);
			var crColorPreview = new CellRendererColorPreview ();
			valueCol.PackStart (crColorPreview, false);
			valueCol.SetCellDataFunc (crColorPreview, new TreeCellDataFunc ((tree_column, cell, model, iter) => {
				var color = model.GetValue (iter, ColorPreviewColumn);
				if (color != null) {
					((CellRendererColorPreview)cell).Color = (Xwt.Drawing.Color)color;
					cell.Visible = true;
				} else {
					cell.Visible = false;
				}
			}));
			crpButton = new CellRendererRoundedButton ();
			valueCol.PackStart (crpButton, false);
			valueCol.AddAttribute (crpButton, "visible", ValueButtonVisibleColumn);
			valueCol.AddAttribute (crpButton, "text", ValueButtonTextColumn);
			crpViewer = new CellRendererImage ();
			crpViewer.Image = ImageService.GetIcon (Stock.Edit, IconSize.Menu);
			valueCol.PackStart (crpViewer, false);
			valueCol.AddAttribute (crpViewer, "visible", ViewerButtonVisibleColumn);
			crtValue = new CellRendererTextUrl ();
			valueCol.PackStart (crtValue, true);
			valueCol.AddAttribute (crtValue, "texturl", ValueColumn);
			valueCol.AddAttribute (crtValue, "editable", ValueEditableColumn);
			valueCol.AddAttribute (crtValue, "foreground", ValueColorColumn);
			valueCol.Resizable = true;
			valueCol.MinWidth = 15;
			valueCol.AddNotification ("width", OnColumnWidthChanged);
//			valueCol.Expand = true;
			valueCol.Sizing = TreeViewColumnSizing.Fixed;
			AppendColumn (valueCol);
			
			typeCol = new TreeViewColumn ();
			typeCol.Title = GettextCatalog.GetString ("Type");
			crtType = new CellRendererText ();
			typeCol.PackStart (crtType, true);
			typeCol.AddAttribute (crtType, "text", TypeColumn);
			typeCol.Resizable = true;
			typeCol.Sizing = TreeViewColumnSizing.Fixed;
			typeCol.MinWidth = 15;
			typeCol.AddNotification ("width", OnColumnWidthChanged);
//			typeCol.Expand = true;
			AppendColumn (typeCol);
			
			pinCol = new TreeViewColumn ();
			crpPin = new CellRendererImage ();
			pinCol.PackStart (crpPin, false);
			pinCol.AddAttribute (crpPin, "stock_id", PinIconColumn);
			crpLiveUpdate = new CellRendererImage ();
			pinCol.PackStart (crpLiveUpdate, false);
			pinCol.AddAttribute (crpLiveUpdate, "image", LiveUpdateIconColumn);
			pinCol.Resizable = false;
			pinCol.Visible = false;
			pinCol.Expand = false;
			AppendColumn (pinCol);
			
			state = new TreeViewState (this, NameColumn);
			
			crtExp.Edited += OnExpEdited;
			crtExp.EditingStarted += OnExpEditing;
			crtExp.EditingCanceled += OnEditingCancelled;
			crtValue.EditingStarted += OnValueEditing;
			crtValue.Edited += OnValueEdited;
			crtValue.EditingCanceled += OnEditingCancelled;
			
			this.EnableAutoTooltips ();
			
			createMsg = GettextCatalog.GetString ("Click here to add a new watch");
			CompletionWindowManager.WindowClosed += HandleCompletionWindowClosed;
			PreviewWindowManager.WindowClosed += HandlePreviewWindowClosed;
		}

		void HandlePreviewWindowClosed (object sender, EventArgs e)
		{
			SetPreviewButtonIcon (PreviewButtonIcons.Hidden);
		}

		void HandleCompletionWindowClosed (object sender, EventArgs e)
		{
			currentCompletionData = null;
		}

		protected override void OnDestroyed ()
		{
			CompletionWindowManager.WindowClosed -= HandleCompletionWindowClosed;
			PreviewWindowManager.WindowClosed -= HandlePreviewWindowClosed;
			crtExp.Edited -= OnExpEdited;
			crtExp.EditingStarted -= OnExpEditing;
			crtExp.EditingCanceled -= OnEditingCancelled;
			crtValue.EditingStarted -= OnValueEditing;
			crtValue.Edited -= OnValueEdited;
			crtValue.EditingCanceled -= OnEditingCancelled;

			typeCol.RemoveNotification ("width", OnColumnWidthChanged);
			valueCol.RemoveNotification ("width", OnColumnWidthChanged);
			expCol.RemoveNotification ("width", OnColumnWidthChanged);

			values.Clear ();
			valueNames.Clear ();
			Frame = null;

			disposed = true;
			cancellationTokenSource.Cancel ();

			base.OnDestroyed ();
		}
		
		protected override void OnSizeAllocated (Gdk.Rectangle allocation)
		{
			base.OnSizeAllocated (allocation);
			AdjustColumnSizes ();
		}
		
		protected override void OnShown ()
		{
			base.OnShown ();
			AdjustColumnSizes ();
		}

		protected override void OnRealized ()
		{
			base.OnRealized ();
			AdjustColumnSizes ();
		}

		void OnColumnWidthChanged (object o, GLib.NotifyArgs args)
		{
			if (!columnSizesUpdating && allowStoreColumnSizes) {
				StoreColumnSizes ();
			}
		}

		void AdjustColumnSizes ()
		{
			if (!Visible || Allocation.Width <= 0 || columnSizesUpdating || compact)
				return;
			
			columnSizesUpdating = true;
			
			double width = (double) Allocation.Width;
			
			int texp = Math.Max ((int) (width * expColWidth), 1);
			if (texp != expCol.FixedWidth) {
				expCol.FixedWidth = texp;
			}
			
			if (typeCol.Visible) {
				int ttype = Math.Max ((int) (width * typeColWidth), 1);
				if (ttype != typeCol.FixedWidth) {
					typeCol.FixedWidth = ttype;
				}
			}
			
			int tval = Math.Max ((int) (width * valueColWidth), 1);

			if (tval != valueCol.FixedWidth) {
				valueCol.FixedWidth = tval;
				Application.Invoke (delegate { QueueResize (); });
			}
			
			columnSizesUpdating = false;
			columnsAdjusted = true;
		}
		
		void StoreColumnSizes ()
		{
			if (!IsRealized || !Visible || !columnsAdjusted || compact)
				return;
			
			double width = (double) Allocation.Width;
			expColWidth = ((double) expCol.Width) / width;
			valueColWidth = ((double) valueCol.Width) / width;
			if (typeCol.Visible)
				typeColWidth = ((double) typeCol.Width) / width;
		}
		
		void ResetColumnSizes ()
		{
			expColWidth = 0.3;
			valueColWidth = 0.5;
			typeColWidth = 0.2;
		}
		
		public StackFrame Frame {
			get {
				return frame;
			}
			set {
				frame = value;
				Update ();
			}
		}
				
		public void SaveState ()
		{
			state.Save ();
		}
		
		public void LoadState ()
		{
			restoringState = true;
			state.Load ();
			restoringState = false;
		}

		bool allowAdding;
		public bool AllowAdding {
			get {
				return allowAdding;
			}
			set {
				allowAdding = value;
				Refresh (false);
			}
		}

		bool allowEditing;
		public bool AllowEditing {
			get {
				return allowEditing;
			}
			set {
				allowEditing = value;
				Refresh (false);
			}
		}
		
		public bool AllowPinning {
			get { return pinCol.Visible; }
			set { pinCol.Visible = value; }
		}
		
		public bool RootPinAlwaysVisible { get; set; }

		bool allowExpanding = true;
		public bool AllowExpanding {
			get { return allowExpanding; }
			set { allowExpanding = value; }
		}

		public bool AllowPopupMenu {
			get; set;
		}

		static bool CanQueryDebugger {
			get {
				return DebuggingService.IsConnected && DebuggingService.IsPaused;
			}
		}
		
		public PinnedWatch PinnedWatch { get; set; }
		
		public string PinnedWatchFile { get; set; }
		public int PinnedWatchLine { get; set; }
		
		public bool CompactView {
			get {
				return compact; 
			}
			set {
				compact = value;
				Pango.FontDescription newFont;
				if (compact) {
					newFont = Style.FontDescription.Copy ();
					newFont.Size = (newFont.Size * 8) / 10;
					expCol.Sizing = TreeViewColumnSizing.Autosize;
					valueCol.Sizing = TreeViewColumnSizing.Autosize;
					valueCol.MaxWidth = 800;
					crpViewer.Image = ImageService.GetIcon (Stock.Edit).WithSize (12,12);
					ColumnsAutosize ();
				} else {
					newFont = Style.FontDescription;
					expCol.Sizing = TreeViewColumnSizing.Fixed;
					valueCol.Sizing = TreeViewColumnSizing.Fixed;
					valueCol.MaxWidth = int.MaxValue;
				}
				typeCol.Visible = !compact;
				crtExp.FontDesc = newFont;
				crtValue.FontDesc = newFont;
				crtType.FontDesc = newFont;
				ResetColumnSizes ();
				AdjustColumnSizes ();
			}
		}
		
		public void AddExpression (string exp)
		{
			valueNames.Add (exp);
			Refresh (false);
		}
		
		public void AddExpressions (IEnumerable<string> exps)
		{
			valueNames.AddRange (exps);
			Refresh (false);
		}
		
		public void RemoveExpression (string exp)
		{
			cachedValues.Remove (exp);
			valueNames.Remove (exp);
			Refresh (true);
		}
		
		public void AddValue (ObjectValue value)
		{
			values.Add (value);
			Refresh (false);
		}
		
		public void AddValues (IEnumerable<ObjectValue> newValues)
		{
			foreach (ObjectValue val in newValues)
				values.Add (val);
			Refresh (false);
		}
		
		public void RemoveValue (ObjectValue value)
		{
			values.Remove (value);
			Refresh (true);
		}

		public void ReplaceValue (ObjectValue old, ObjectValue @new)
		{
			int idx = values.IndexOf (old);
			if (idx == -1)
				return;

			values [idx] = @new;
			Refresh (false);
		}

		public void ClearAll ()
		{
			values.Clear ();
			valueNames.Clear ();
			cachedValues.Clear ();
			frame = null;
			Refresh (true);
		}
		
		public void ClearValues ()
		{
			values.Clear ();
			Refresh (true);
		}
		
		public void ClearExpressions ()
		{
			valueNames.Clear ();
			Update ();
		}
		
		public IEnumerable<string> Expressions {
			get { return valueNames; }
		}
		
		public void Update ()
		{
			cachedValues.Clear ();
			Refresh (true);
		}
		
		void Refresh (bool resetScrollPosition)
		{
			foreach (ObjectValue val in new List<ObjectValue> (nodes.Keys))
				UnregisterValue (val);
			nodes.Clear ();

			// Note: this is a hack that ideally we could get rid of...
			if (IsRealized && resetScrollPosition)
				ScrollToPoint (0, 0);

			SaveState ();

			CleanPinIcon ();
			store.Clear ();
			
			bool showExpanders = AllowAdding;

			foreach (ObjectValue val in values) {
				AppendValue (TreeIter.Zero, null, val);
				if (val.HasChildren)
					showExpanders = true;
			}
			
			if (valueNames.Count > 0) {
				ObjectValue[] expValues = GetValues (valueNames.ToArray ());
				for (int n = 0; n < expValues.Length; n++) {
					AppendValue (TreeIter.Zero, valueNames[n], expValues[n]);
					if (expValues[n].HasChildren)
						showExpanders = true;
				}
			}

			if (showExpanders)
				ShowExpanders = true;
			
			if (AllowAdding)
				store.AppendValues (createMsg, "", "", null, true, true, null, disabledColor, disabledColor);

			LoadState ();
		}

		public void Refresh ()
		{
			Refresh (true);
		}
		
		void RefreshRow (TreeIter iter)
		{
			var val = (ObjectValue) store.GetValue (iter, ObjectColumn);
			UnregisterValue (val);
			
			RemoveChildren (iter);
			TreeIter parent;
			if (!store.IterParent (out parent, iter))
				parent = TreeIter.Zero;

			if (CanQueryDebugger && frame != null) {
				EvaluationOptions ops = frame.DebuggerSession.Options.EvaluationOptions.Clone ();
				ops.AllowMethodEvaluation = true;
				ops.AllowToStringCalls = true;
				ops.AllowTargetInvoke = true;
				ops.EllipsizeStrings = false;

				string oldName = val.Name;
				val.Refresh (ops);

				// Don't update the name for the values entered by the user
				if (store.IterDepth (iter) == 0)
					val.Name = oldName;
			}

			SetValues (parent, iter, val.Name, val);
			RegisterValue (val, iter);
		}

		void RemoveChildren (TreeIter iter)
		{
			TreeIter citer;

			while (store.IterChildren (out citer, iter)) {
				var val = (ObjectValue) store.GetValue (citer, ObjectColumn);
				if (val != null)
					UnregisterValue (val);
				RemoveChildren (citer);
				store.Remove (ref citer);
			}
		}

		void RegisterValue (ObjectValue val, TreeIter iter)
		{
			if (val.IsEvaluating) {
				nodes [val] = new TreeRowReference (store, store.GetPath (iter));
				val.ValueChanged += OnValueUpdated;
			}
		}

		void UnregisterValue (ObjectValue val)
		{
			val.ValueChanged -= OnValueUpdated;
			nodes.Remove (val);
		}

		void OnValueUpdated (object o, EventArgs a)
		{
			Application.Invoke (delegate {
				if (disposed)
					return;

				var val = (ObjectValue) o;
				TreeIter it;

				if (FindValue (val, out it)) {
					// Keep the expression name entered by the user
					if (store.IterDepth (it) == 0)
						val.Name = (string) store.GetValue (it, NameColumn);

					RemoveChildren (it);
					TreeIter parent;

					if (!store.IterParent (out parent, it))
						parent = TreeIter.Zero;
					
					// If it was an evaluating group, replace the node with the new nodes
					if (val.IsEvaluatingGroup) {
						if (val.ArrayCount == 0) {
							store.Remove (ref it);
						} else {
							SetValues (parent, it, null, val.GetArrayItem (0));
							RegisterValue (val, it);
							for (int n=1; n<val.ArrayCount; n++) {
								TreeIter cit = store.InsertNodeAfter (it);
								ObjectValue cval = val.GetArrayItem (n);
								SetValues (parent, cit, null, cval);
								RegisterValue (cval, cit);
							}
						}
					} else {
						SetValues (parent, it, val.Name, val);
					}
				}
				UnregisterValue (val);
			});
		}

		bool FindValue (ObjectValue val, out TreeIter it)
		{
			TreeRowReference row;
			
			if (!nodes.TryGetValue (val, out row) || !row.Valid ()) {
				it = TreeIter.Zero;
				return false;
			}
			
			return store.GetIter (out it, row.Path);
		}
		
		public void ResetChangeTracking ()
		{
			oldValues.Clear ();
		}
		
		public void ChangeCheckpoint ()
		{
			oldValues.Clear ();
			
			TreeIter it;
			if (!store.GetIterFirst (out it))
				return;
			
			ChangeCheckpoint (it, "/");
		}
		
		void ChangeCheckpoint (TreeIter it, string path)
		{
			do {
				string name = (string) store.GetValue (it, NameColumn);
				string val = (string) store.GetValue (it, ValueColumn);
				oldValues [path + name] = val;
				TreeIter cit;
				if (store.IterChildren (out cit, it))
					ChangeCheckpoint (cit, name + "/");
			} while (store.IterNext (ref it));
		}
		
		void AppendValue (TreeIter parent, string name, ObjectValue val)
		{
			TreeIter iter;

			if (parent.Equals (TreeIter.Zero))
				iter = store.AppendNode ();
			else
				iter = store.AppendNode (parent);

			SetValues (parent, iter, name, val);
			RegisterValue (val, iter);
		}
		
		void SetValues (TreeIter parent, TreeIter it, string name, ObjectValue val)
		{
			string strval;
			bool canEdit;
			string nameColor = null;
			string valueColor = null;
			string valueButton = null;
			string evaluateStatusIcon = null;
			
			name = name ?? val.Name;

			bool hasParent = !parent.Equals (TreeIter.Zero);
			bool showViewerButton = false;
			
			string valPath;
			if (!hasParent)
				valPath = "/" + name;
			else
				valPath = GetIterPath (parent) + "/" + name;
			
			string oldValue;
			oldValues.TryGetValue (valPath, out oldValue);
			
			if (val.IsUnknown) {
				if (frame != null) {
					strval = GettextCatalog.GetString ("The name '{0}' does not exist in the current context.", val.Name);
					nameColor = disabledColor;
					canEdit = false;
				} else {
					canEdit = !val.IsReadOnly;
					strval = string.Empty;
				}
				evaluateStatusIcon = MonoDevelop.Ide.Gui.Stock.Warning;
			} else if (val.IsError) {
				evaluateStatusIcon = MonoDevelop.Ide.Gui.Stock.Warning;
				strval = val.Value;
				int i = strval.IndexOf ('\n');
				if (i != -1)
					strval = strval.Substring (0, i);
				valueColor = errorColor;
				canEdit = false;
			} else if (val.IsNotSupported) {
				strval = val.Value;
				valueColor = disabledColor;
				if (val.CanRefresh)
					valueButton = GettextCatalog.GetString ("Show Value");
				canEdit = false;
			} else if (val.IsEvaluating) {
				strval = GettextCatalog.GetString ("Evaluating...");
				evaluateStatusIcon = "md-spinner-14";
				valueColor = disabledColor;
				if (val.IsEvaluatingGroup) {
					nameColor = disabledColor;
					name = val.Name;
				}
				canEdit = false;
			} else {
				showViewerButton = !val.IsNull && DebuggingService.HasValueVisualizers (val);
				canEdit = val.IsPrimitive && !val.IsReadOnly;
				if (!val.IsNull && DebuggingService.HasInlineVisualizer (val)) {
					strval = DebuggingService.GetInlineVisualizer (val).InlineVisualize (val);
				} else {
					strval = val.DisplayValue ?? "(null)";
				}
				if (oldValue != null && strval != oldValue)
					nameColor = valueColor = modifiedColor;
			}
			
			strval = strval.Replace (Environment.NewLine, " ");

			bool hasChildren = val.HasChildren;
			string icon = GetIcon (val.Flags);
			store.SetValue (it, NameColumn, name);
			store.SetValue (it, ValueColumn, strval);
			store.SetValue (it, TypeColumn, val.TypeName);
			store.SetValue (it, ObjectColumn, val);
			store.SetValue (it, NameEditableColumn, !hasParent && AllowAdding);
			store.SetValue (it, ValueEditableColumn, canEdit && AllowEditing);
			store.SetValue (it, IconColumn, icon);
			store.SetValue (it, NameColorColumn, nameColor);
			store.SetValue (it, ValueColorColumn, valueColor);
			store.SetValue (it, EvaluateStatusIconVisibleColumn, evaluateStatusIcon != null);
			store.LoadIcon (it, EvaluateStatusIconColumn, evaluateStatusIcon, IconSize.Menu);
			store.SetValue (it, ValueButtonVisibleColumn, valueButton != null);
			store.SetValue (it, ValueButtonTextColumn, valueButton);
			store.SetValue (it, ViewerButtonVisibleColumn, showViewerButton);

			if (!val.IsNull && DebuggingService.HasGetConverter<Xwt.Drawing.Color> (val))
				store.SetValue (it, ColorPreviewColumn, DebuggingService.GetGetConverter<Xwt.Drawing.Color> (val).GetValue (val));
			else
				store.SetValue (it, ColorPreviewColumn, null);

			if (!hasParent && PinnedWatch != null) {
				store.SetValue (it, PinIconColumn, "md-pin-down");
				if (PinnedWatch.LiveUpdate)
					store.SetValue (it, LiveUpdateIconColumn, liveIcon);
				else
					store.SetValue (it, LiveUpdateIconColumn, noLiveIcon);
			}
			if (RootPinAlwaysVisible && (!hasParent && PinnedWatch ==null && AllowPinning))
				store.SetValue (it, PinIconColumn, "md-pin-up");
			
			if (hasChildren) {
				// Add dummy node
				store.AppendValues (it, GettextCatalog.GetString ("Loading..."), "", "", null, true);
				if (!ShowExpanders)
					ShowExpanders = true;
			}
		}
		
		public static string GetIcon (ObjectValueFlags flags)
		{
			if ((flags & ObjectValueFlags.Field) != 0 && (flags & ObjectValueFlags.ReadOnly) != 0)
				return "md-literal";

			string global = (flags & ObjectValueFlags.Global) != 0 ? "static-" : string.Empty;
			string source;

			switch (flags & ObjectValueFlags.OriginMask) {
			case ObjectValueFlags.Property: source = "property"; break;
			case ObjectValueFlags.Type: source = "class"; global = string.Empty; break;
			case ObjectValueFlags.Method: source = "method"; break;
			case ObjectValueFlags.Literal: return "md-literal";
			case ObjectValueFlags.Namespace: return "md-name-space";
			case ObjectValueFlags.Group: return "md-open-resource-folder";
			case ObjectValueFlags.Field: source = "field"; break;
			case ObjectValueFlags.Variable: return "md-variable";
			default: return "md-empty";
			}

			string access;
			switch (flags & ObjectValueFlags.AccessMask) {
			case ObjectValueFlags.Private: access = "private-"; break;
			case ObjectValueFlags.Internal: access = "internal-"; break;
			case ObjectValueFlags.InternalProtected:
			case ObjectValueFlags.Protected: access = "protected-"; break;
			default: access = string.Empty; break;
			}
			
			return "md-" + access + global + source;
		}
		
		protected override bool OnTestExpandRow (TreeIter iter, TreePath path)
		{
			if (!restoringState) {
				if (!allowExpanding)
					return true;

				if (GetRowExpanded (path))
					return true;

				TreeIter parent;
				if (store.IterParent (out parent, iter)) {
					if (!GetRowExpanded (store.GetPath (parent)))
						return true;
				}
			}
			
			return base.OnTestExpandRow (iter, path);
		}
		
		protected override void OnRowCollapsed (TreeIter iter, TreePath path)
		{
			base.OnRowCollapsed (iter, path);

			if (compact)
				ColumnsAutosize ();

			ScrollToCell (path, expCol, true, 0f, 0f);
		}

		static Task<ObjectValue[]> GetChildrenAsync (ObjectValue value, CancellationToken cancellationToken)
		{
			return Task.Factory.StartNew<ObjectValue[]> (delegate (object arg) {
				try {
					return ((ObjectValue) arg).GetAllChildren ();
				} catch (Exception ex) {
					// Note: this should only happen if someone breaks ObjectValue.GetAllChildren()
					LoggingService.LogError ("Failed to get ObjectValue children.", ex);
					return new ObjectValue[0];
				}
			}, value, cancellationToken);
		}

		void AddChildrenAsync (ObjectValue value, TreePathReference row)
		{
			Task task;

			if (expandTasks.TryGetValue (value, out task))
				return;

			task = GetChildrenAsync (value, cancellationTokenSource.Token).ContinueWith (t => {
				TreeIter iter, it;

				if (disposed)
					return;

				if (row.IsValid && store.GetIter (out iter, row.Path) && store.IterChildren (out it, iter)) {
					foreach (var child in t.Result) {
						SetValues (iter, it, null, child);
						RegisterValue (child, it);
						it = store.InsertNodeAfter (it);
					}

					store.Remove (ref it);

					if (compact)
						ColumnsAutosize ();
				}

				expandTasks.Remove (value);
				row.Dispose ();
			}, cancellationTokenSource.Token, TaskContinuationOptions.NotOnCanceled, Xwt.Application.UITaskScheduler);
			expandTasks.Add (value, task);
		}
		
		protected override void OnRowExpanded (TreeIter iter, TreePath path)
		{
			TreeIter child;
			
			if (store.IterChildren (out child, iter)) {
				var value = (ObjectValue) store.GetValue (child, ObjectColumn);
				if (value == null) {
					value = (ObjectValue) store.GetValue (iter, ObjectColumn);
					AddChildrenAsync (value, new TreePathReference (store, store.GetPath (iter)));
				}
			}
			
			base.OnRowExpanded (iter, path);

			ScrollToCell (path, expCol, true, 0f, 0f);
		}
		
		string GetIterPath (TreeIter iter)
		{
			var path = new StringBuilder ();

			do {
				string name = (string) store.GetValue (iter, NameColumn);
				path.Insert (0, "/" + name);
			} while (store.IterParent (out iter, iter));

			return path.ToString ();
		}

		void OnExpEditing (object s, EditingStartedArgs args)
		{
			TreeIter iter;

			if (!store.GetIterFromString (out iter, args.Path))
				return;

			var entry = (Entry) args.Editable;
			if (entry.Text == createMsg)
				entry.Text = string.Empty;
			
			OnStartEditing (args);
		}
		
		void OnExpEdited (object s, EditedArgs args)
		{
			OnEndEditing ();
			
			TreeIter iter;
			if (!store.GetIterFromString (out iter, args.Path))
				return;

			if (store.GetValue (iter, ObjectColumn) == null) {
				if (args.NewText.Length > 0) {
					valueNames.Add (args.NewText);
					Refresh (false);
				}
			} else {
				string exp = (string) store.GetValue (iter, NameColumn);
				if (args.NewText == exp)
					return;
				
				int i = valueNames.IndexOf (exp);
				if (i == -1)
					return;

				if (args.NewText.Length != 0)
					valueNames [i] = args.NewText;
				else
					valueNames.RemoveAt (i);
				
				cachedValues.Remove (exp);
				Refresh (true);
			}
		}
		
		bool editing;
		
		void OnValueEditing (object s, EditingStartedArgs args)
		{
			TreeIter it;
			if (!store.GetIterFromString (out it, args.Path))
				return;
			
			var entry = (Entry) args.Editable;
			
			var val = store.GetValue (it, ObjectColumn) as ObjectValue;
			string strVal = val != null ? val.Value : null;
			if (!string.IsNullOrEmpty (strVal))
				entry.Text = strVal;
			
			entry.GrabFocus ();
			OnStartEditing (args);
		}
		
		void OnValueEdited (object s, EditedArgs args)
		{
			OnEndEditing ();
			
			TreeIter it;
			if (!store.GetIterFromString (out it, args.Path))
				return;

			var val = (ObjectValue) store.GetValue (it, ObjectColumn);

			if (val == null)
				return;

			try {
				string newVal = args.NewText;
/*				if (newVal == null) {
					MessageService.ShowError (GettextCatalog.GetString ("Unregognized escape sequence."));
					return;
				}
*/				if (val.Value != newVal)
					val.Value = newVal;
			} catch (Exception ex) {
				LoggingService.LogError ("Could not set value for object '" + val.Name + "'", ex);
			}

			store.SetValue (it, ValueColumn, val.DisplayValue);

			// Update the color
			
			string newColor = null;
			
			string valPath = GetIterPath (it);
			string oldValue;
			if (oldValues.TryGetValue (valPath, out oldValue)) {
				if (oldValue != val.Value)
					newColor = modifiedColor;
			}
			
			store.SetValue (it, NameColorColumn, newColor);
			store.SetValue (it, ValueColorColumn, newColor);
		}
		
		void OnEditingCancelled (object s, EventArgs args)
		{
			OnEndEditing ();
		}
		
		void OnStartEditing (EditingStartedArgs args)
		{
			editing = true;
			editEntry = (Entry) args.Editable;
			editEntry.KeyPressEvent += OnEditKeyPress;
			editEntry.KeyReleaseEvent += OnEditKeyRelease;
			if (StartEditing != null)
				StartEditing (this, EventArgs.Empty);
		}

		void OnEndEditing ()
		{
			editing = false;
			editEntry.KeyPressEvent -= OnEditKeyPress;
			editEntry.KeyReleaseEvent -= OnEditKeyRelease;

			CompletionWindowManager.HideWindow ();
			currentCompletionData = null;
			if (EndEditing != null)
				EndEditing (this, EventArgs.Empty);
		}

		void OnEditKeyRelease (object sender, EventArgs e)
		{
			if (!wasHandled) {
				string text = ctx == null ? editEntry.Text : editEntry.Text.Substring (Math.Max (0, Math.Min (ctx.TriggerOffset, editEntry.Text.Length)));
				CompletionWindowManager.UpdateWordSelection (text);
				CompletionWindowManager.PostProcessKeyEvent (key, keyChar, modifierState);
				PopupCompletion ((Entry) sender);
			}
		}

		bool wasHandled;
		CodeCompletionContext ctx;
		Gdk.Key key;
		char keyChar;
		Gdk.ModifierType modifierState;
		uint keyValue;

		[GLib.ConnectBeforeAttribute]
		void OnEditKeyPress (object s, KeyPressEventArgs args)
		{
			wasHandled = false;
			key = args.Event.Key;
			keyChar =  (char)args.Event.Key;
			modifierState = args.Event.State;
			keyValue = args.Event.KeyValue;

			if (currentCompletionData != null) {
				wasHandled  = CompletionWindowManager.PreProcessKeyEvent (key, keyChar, modifierState);
				args.RetVal = wasHandled;
			}
		}

		static bool IsCompletionChar (char c)
		{
			return (char.IsLetterOrDigit (c) || char.IsPunctuation (c) || char.IsSymbol (c) || char.IsWhiteSpace (c));
		}

		void PopupCompletion (Entry entry)
		{
			Application.Invoke (delegate {
				char c = (char)Gdk.Keyval.ToUnicode (keyValue);
				if (currentCompletionData == null && IsCompletionChar (c)) {
					string exp = entry.Text.Substring (0, entry.CursorPosition);
					currentCompletionData = GetCompletionData (exp);
					if (currentCompletionData != null) {
						var dataList = new DebugCompletionDataList (currentCompletionData);
						ctx = ((ICompletionWidget)this).CreateCodeCompletionContext (entry.CursorPosition - currentCompletionData.ExpressionLength);
						CompletionWindowManager.ShowWindow (null, c, dataList, this, ctx);
					} else {
						currentCompletionData = null;
					}
				}
			});
		}

		TreeIter lastPinIter;

		enum PreviewButtonIcons
		{
			Hidden,
			RowHover,
			Hover,
			Active
		}

		PreviewButtonIcons currentIcon;
		TreeIter currentHoverIter = TreeIter.Zero;

		void SetPreviewButtonIcon (PreviewButtonIcons icon, TreeIter it = default(TreeIter))
		{
			if (!it.Equals (TreeIter.Zero)) {
				var obj = Model.GetValue (it, ObjectColumn) as ObjectValue;
				if (obj == null) {
					icon = PreviewButtonIcons.Hidden;
				} else {
					if (obj.IsPrimitive && obj.TypeName != "string")
						icon = PreviewButtonIcons.Hidden;
					if (obj.IsNull)
						icon = PreviewButtonIcons.Hidden;
					if (string.IsNullOrEmpty (obj.TypeName))
						icon = PreviewButtonIcons.Hidden;
				}
			}
			if (PreviewWindowManager.IsVisible && icon != PreviewButtonIcons.Active) {
				return;
			}
			if (currentIcon != icon || !currentHoverIter.Equals (it)) {
				if (!currentHoverIter.Equals (TreeIter.Zero)) {
					store.SetValue (currentHoverIter, PreviewIconColumn, null);
				}
				switch (icon) {
				case PreviewButtonIcons.Hidden:
					store.SetValue (it, PreviewIconColumn, null);
					break;
				case PreviewButtonIcons.RowHover:
					store.SetValue (it, PreviewIconColumn, "md-preview-normal");
					break;
				case PreviewButtonIcons.Hover:
					store.SetValue (it, PreviewIconColumn, "md-preview-hover");
					break;
				case PreviewButtonIcons.Active:
					store.SetValue (it, PreviewIconColumn, "md-preview-active");
					break;
				}
				currentIcon = icon;
				currentHoverIter = it;
			}
		}


		protected override bool OnMotionNotifyEvent (Gdk.EventMotion evnt)
		{
			TreePath path;
			if (!editing && GetPathAtPos ((int)evnt.X, (int)evnt.Y, out path)) {

				TreeIter it;
				if (store.GetIter (out it, path)) {
					TreeViewColumn col;
					CellRenderer cr;
					if (GetCellAtPos ((int)evnt.X, (int)evnt.Y, out path, out col, out cr) && cr == crtExp) {
						var layout = new Pango.Layout (PangoContext);
						layout.FontDescription = crtExp.FontDesc.Copy ();
						layout.FontDescription.Family = crtExp.Family;
						layout.SetText ((string)store.GetValue (it, NameColumn));
						int w, h;
						layout.GetPixelSize (out w, out h);
						var cellArea = GetCellRendererArea (path, col, cr);
						var iconXOffset = cellArea.X + w + cr.Xpad * 3;
						if (iconXOffset < evnt.X &&
						     iconXOffset + 16 > evnt.X) {
							SetPreviewButtonIcon (PreviewButtonIcons.Hover, it);
						} else {
							SetPreviewButtonIcon (PreviewButtonIcons.RowHover, it);
						}

					} else {
						SetPreviewButtonIcon (PreviewButtonIcons.RowHover, it);
					}

					if (AllowPinning) {
						if (path.Depth > 1 || PinnedWatch == null) {
							if (!it.Equals (lastPinIter)) {
								store.SetValue (it, PinIconColumn, "md-pin-up");
								CleanPinIcon ();
								if (path.Depth > 1 || !RootPinAlwaysVisible)
									lastPinIter = it;
							}
						}
					}
				}
			} else {
				SetPreviewButtonIcon (PreviewButtonIcons.Hidden);
			}
			return base.OnMotionNotifyEvent (evnt);
		}
		
		void CleanPinIcon ()
		{
			if (!lastPinIter.Equals (TreeIter.Zero)) {
				store.SetValue (lastPinIter, PinIconColumn, null);
				lastPinIter = TreeIter.Zero;
			}
		}
		
		protected override bool OnLeaveNotifyEvent (Gdk.EventCrossing evnt)
		{
			if (!editing)
				CleanPinIcon ();
			SetPreviewButtonIcon (PreviewButtonIcons.Hidden);
			return base.OnLeaveNotifyEvent (evnt);
		}
		
		protected override bool OnKeyPressEvent (Gdk.EventKey evnt)
		{
			// Ignore if editing a cell
			if (editing)
				return base.OnKeyPressEvent (evnt);

			TreePath[] selected = Selection.GetSelectedRows ();
			bool changed = false;
			TreePath lastPath;

			if (selected == null || selected.Length < 1)
				return base.OnKeyPressEvent (evnt);

			switch (evnt.Key) {
			case Gdk.Key.Left:
			case Gdk.Key.KP_Left:
				foreach (var path in selected) {
					lastPath = path.Copy ();
					if (GetRowExpanded (path)) {
						CollapseRow (path);
						changed = true;
					} else if (path.Up ()) {
						Selection.UnselectPath (lastPath);
						Selection.SelectPath (path);
						changed = true;
					}
				}
				break;
			case Gdk.Key.Right:
			case Gdk.Key.KP_Right:
				foreach (var path in selected) {
					if (!GetRowExpanded (path)) {
						ExpandRow (path, false);
						changed = true;
					} else {
						lastPath = path.Copy ();
						path.Down ();
						if (lastPath.Compare (path) != 0) {
							Selection.UnselectPath (lastPath);
							Selection.SelectPath (path);
							changed = true;
						}
					}
				}
				break;
			case Gdk.Key.Delete:
			case Gdk.Key.KP_Delete:
			case Gdk.Key.BackSpace:
				string expression;
				ObjectValue val;
				TreeIter iter;

				if (!AllowEditing || !AllowAdding)
					return base.OnKeyPressEvent (evnt);

				// Note: since we'll be modifying the tree, we need to make changes from bottom to top
				Array.Sort (selected, new TreePathComparer (true));

				foreach (var path in selected) {
					if (!Model.GetIter (out iter, path))
						continue;

					val = (ObjectValue)store.GetValue (iter, ObjectColumn);
					expression = GetFullExpression (iter);

					// Lookup and remove
					if (val != null && values.Contains (val)) {
						RemoveValue (val);
						changed = true;
					} else if (!string.IsNullOrEmpty (expression) && valueNames.Contains (expression)) {
						RemoveExpression (expression);
						changed = true;
					}
				}
				break;
			}

			return changed || base.OnKeyPressEvent (evnt);
		}

		Gdk.Rectangle GetCellRendererArea (TreePath path, TreeViewColumn col, CellRenderer cr)
		{
			var rect = this.GetCellArea (path, col);
			int x, width;
			col.CellGetPosition (cr, out x, out width);
			return new Gdk.Rectangle (rect.X + x, rect.Y, width, rect.Height);
		}

		protected override bool OnButtonPressEvent (Gdk.EventButton evnt)
		{
			allowStoreColumnSizes = true;
			bool retval = base.OnButtonPressEvent (evnt);
			
			//HACK: show context menu in release event instead of show event to work around gtk bug
			if (evnt.TriggersContextMenu ()) {
			//	ShowPopup (evnt);
				return true;
			}

			TreeViewColumn col;
			CellRenderer cr;
			TreePath path;
			
			if (CanQueryDebugger && evnt.Button == 1 && GetCellAtPos ((int)evnt.X, (int)evnt.Y, out path, out col, out cr)) {
				TreeIter it;
				store.GetIter (out it, path);
				if (cr == crpViewer) {
					var val = (ObjectValue)store.GetValue (it, ObjectColumn);
					DebuggingService.ShowValueVisualizer (val);
				} else if (cr == crtExp && (store.GetValue (it, PreviewIconColumn) as string) == "md-preview-hover") {
					var val = (ObjectValue)store.GetValue (it, ObjectColumn);
					var rect = GetCellRendererArea (path, col, cr);
					var layout = new Pango.Layout (PangoContext);
					layout.FontDescription = crtExp.FontDesc.Copy ();
					layout.FontDescription.Family = crtExp.Family;
					layout.SetText ((string)store.GetValue (it, NameColumn));
					int w, h;
					layout.GetPixelSize (out w, out h);
					rect.X += (int)(w + cr.Xpad * 3);
					rect.Width = 16;
					ConvertTreeToWidgetCoords (rect.X, rect.Y, out rect.X, out rect.Y);
					rect.X += (int)Hadjustment.Value;
					rect.Y += (int)Vadjustment.Value;
					DebuggingService.ShowPreviewVisualizer (val, this, rect);
					SetPreviewButtonIcon (PreviewButtonIcons.Active, it);
				} else if (cr == crtValue) {
					if ((Platform.IsMac && ((evnt.State & Gdk.ModifierType.Mod2Mask) > 0)) ||
						(!Platform.IsMac && ((evnt.State & Gdk.ModifierType.ControlMask) > 0))) {
						var url = crtValue.Text.Trim ('"', '{', '}');
						Uri uri;
						if (url != null && Uri.TryCreate (url, UriKind.Absolute, out uri) && (uri.Scheme == "http" || uri.Scheme == "https")) {
							DesktopService.ShowUrl (url);
						}
					}
				} else if (!editing) {
					if (cr == crpButton) {
						RefreshRow (it);
					} else if (cr == crpPin) {
						TreeIter pi;
						if (PinnedWatch != null && !store.IterParent (out pi, it))
							RemovePinnedWatch (it);
						else
							CreatePinnedWatch (it);
					} else if (cr == crpLiveUpdate) {
						TreeIter pi;
						if (PinnedWatch != null && !store.IterParent (out pi, it)) {
							DebuggingService.SetLiveUpdateMode (PinnedWatch, !PinnedWatch.LiveUpdate);
							if (PinnedWatch.LiveUpdate)
								store.SetValue (it, LiveUpdateIconColumn, liveIcon);
							else
								store.SetValue (it, LiveUpdateIconColumn, noLiveIcon);
						}
					}
				}
			}
			
			return retval;
		}
		
		protected override bool OnButtonReleaseEvent (Gdk.EventButton evnt)
		{
			allowStoreColumnSizes = false;
			var res = base.OnButtonReleaseEvent (evnt);
			
			//HACK: show context menu in release event instead of show event to work around gtk bug
			if (evnt.IsContextMenuButton ()) {
				ShowPopup (evnt);
				return true;
			}
			return res;
		}
		
		protected override bool OnPopupMenu ()
		{
			ShowPopup (null);
			return true;
		}
		
		void ShowPopup (Gdk.EventButton evt)
		{
			if (AllowPopupMenu)
				IdeApp.CommandService.ShowContextMenu (this, evt, menuSet, this);
		}

		[CommandUpdateHandler (EditCommands.SelectAll)]
		protected void UpdateSelectAll (CommandInfo cmd)
		{
			TreeIter iter;

			cmd.Enabled = store.GetIterFirst (out iter);
		}

		[CommandHandler (EditCommands.SelectAll)]
		protected new void OnSelectAll ()
		{
			Selection.SelectAll ();
		}
		
		[CommandHandler (EditCommands.Copy)]
		protected void OnCopy ()
		{
			TreePath[] selected = Selection.GetSelectedRows ();
			TreeIter iter;
			
			if (selected == null || selected.Length == 0)
				return;

			if (selected.Length == 1) {
				var editable = IdeApp.Workbench.RootWindow.Focus as Editable;

				if (editable != null) {
					editable.CopyClipboard ();
					return;
				}
			}

			var values = new List<string> ();
			var names = new List<string> ();
			var types = new List<string> ();
			int maxValue = 0;
			int maxName = 0;

			for (int i = 0; i < selected.Length; i++) {
				if (!store.GetIter (out iter, selected[i]))
					continue;

				string value = (string) store.GetValue (iter, ValueColumn);
				string name = (string) store.GetValue (iter, NameColumn);
				string type = (string) store.GetValue (iter, TypeColumn);

				maxValue = Math.Max (maxValue, value.Length);
				maxName = Math.Max (maxName, name.Length);

				values.Add (value);
				names.Add (name);
				types.Add (type);
			}

			var str = new StringBuilder ();
			for (int i = 0; i < values.Count; i++) {
				if (i > 0)
					str.AppendLine ();

				str.Append (names[i]);
				if (names[i].Length < maxName)
					str.Append (new string (' ', maxName - names[i].Length));
				str.Append ('\t');
				str.Append (values[i]);
				if (values[i].Length < maxValue)
					str.Append (new string (' ', maxValue - values[i].Length));
				str.Append ('\t');
				str.Append (types[i]);
			}

			Clipboard.Get (Gdk.Selection.Clipboard).Text = str.ToString ();
		}
		
		[CommandHandler (EditCommands.Delete)]
		[CommandHandler (EditCommands.DeleteKey)]
		protected void OnDelete ()
		{
			foreach (TreePath tp in Selection.GetSelectedRows ()) {
				TreeIter it;
				if (store.GetIter (out it, tp)) {
					string exp = (string) store.GetValue (it, NameColumn);
					cachedValues.Remove (exp);
					valueNames.Remove (exp);
				}
			}
			Refresh (true);
		}
		
		[CommandUpdateHandler (EditCommands.Delete)]
		[CommandUpdateHandler (EditCommands.DeleteKey)]
		protected void OnUpdateDelete (CommandInfo cinfo)
		{
			if (editing) {
				cinfo.Bypass = true;
				return;
			}
			
			if (!AllowAdding) {
				cinfo.Visible = false;
				return;
			}

			TreePath[] sel = Selection.GetSelectedRows ();
			if (sel.Length == 0) {
				cinfo.Enabled = false;
				return;
			}

			foreach (TreePath tp in sel) {
				if (tp.Depth > 1) {
					cinfo.Enabled = false;
					return;
				}
			}
		}

		[CommandHandler (DebugCommands.AddWatch)]
		protected void OnAddWatch ()
		{
			var expressions = new List<string> ();

			foreach (TreePath tp in Selection.GetSelectedRows ()) {
				TreeIter it;

				if (store.GetIter (out it, tp)) {
					var expression = GetFullExpression (it);

					if (!string.IsNullOrEmpty (expression))
						expressions.Add (expression);
				}
			}

			foreach (string expr in expressions)
				DebuggingService.AddWatch (expr);
		}

		[CommandUpdateHandler (DebugCommands.AddWatch)]
		protected void OnUpdateAddWatch (CommandInfo cinfo)
		{
			cinfo.Enabled = Selection.GetSelectedRows ().Length > 0;
		}
		
		[CommandHandler (EditCommands.Rename)]
		protected void OnRename ()
		{
			TreeIter it;
			if (store.GetIter (out it, Selection.GetSelectedRows ()[0]))
				SetCursor (store.GetPath (it), Columns[0], true);
		}
		
		[CommandUpdateHandler (EditCommands.Rename)]
		protected void OnUpdateRename (CommandInfo cinfo)
		{
			cinfo.Visible = AllowAdding;
			cinfo.Enabled = Selection.GetSelectedRows ().Length == 1;
		}
		
		protected override void OnRowActivated (TreePath path, TreeViewColumn column)
		{
			base.OnRowActivated (path, column);

			if (!CanQueryDebugger)
				return;

			TreePath[] selected = Selection.GetSelectedRows ();
			TreeIter iter;

			if (!store.GetIter (out iter, selected[0]))
				return;

			var val = (ObjectValue) store.GetValue (iter, ObjectColumn);
			if (val != null && val.Name == DebuggingService.DebuggerSession.EvaluationOptions.CurrentExceptionTag)
				DebuggingService.ShowExceptionCaughtDialog ();
		}
		
		
		bool GetCellAtPos (int x, int y, out TreePath path, out TreeViewColumn col, out CellRenderer cellRenderer)
		{
			if (GetPathAtPos (x, y, out path, out col)) {
				var cellArea = GetCellArea (path, col);
				x -= cellArea.X;
				foreach (CellRenderer cr in col.CellRenderers) {
					int xo, w;
					col.CellGetPosition (cr, out xo, out w);
					if (cr.Visible && x >= xo && x < xo + w) {
						cellRenderer = cr;
						return true;
					}
				}
			}
			cellRenderer = null;
			return false;
		}
		
		string GetFullExpression (TreeIter it)
		{
			TreePath path = store.GetPath (it);
			string name, expression = "";
			
			while (path.Depth != 1) {
				var val = (ObjectValue) store.GetValue (it, ObjectColumn);
				if (val == null)
					return null;

				expression = val.ChildSelector + expression;
				if (!store.IterParent (out it, it))
					break;

				path = store.GetPath (it);
			}

			name = (string) store.GetValue (it, NameColumn);

			return name + expression;
		}

		public void CreatePinnedWatch (TreeIter it)
		{
			var expression = GetFullExpression (it);

			if (string.IsNullOrEmpty (expression))
				return;

			var watch = new PinnedWatch ();

			if (PinnedWatch != null) {
				CollapseAll ();
				watch.File = PinnedWatch.File;
				watch.Line = PinnedWatch.Line;
				watch.OffsetX = PinnedWatch.OffsetX;
				watch.OffsetY = PinnedWatch.OffsetY + SizeRequest ().Height + 5;
			} else {
				watch.File = PinnedWatchFile;
				watch.Line = PinnedWatchLine;
				watch.OffsetX = -1; // means that the watch should be placed at the line coordinates defined by watch.Line
				watch.OffsetY = -1;
			}

			watch.Expression = expression;
			DebuggingService.PinnedWatches.Add (watch);

			if (PinStatusChanged != null)
				PinStatusChanged (this, EventArgs.Empty);
		}
		
		public void RemovePinnedWatch (TreeIter it)
		{
			DebuggingService.PinnedWatches.Remove (PinnedWatch);
			if (PinStatusChanged != null)
				PinStatusChanged (this, EventArgs.Empty);
		}

		#region ICompletionWidget implementation 
		
		CodeCompletionContext ICompletionWidget.CurrentCodeCompletionContext {
			get {
				return ((ICompletionWidget)this).CreateCodeCompletionContext (editEntry.Position);
			}
		}
		
		public event EventHandler CompletionContextChanged;

		protected virtual void OnCompletionContextChanged (EventArgs e)
		{
			var handler = CompletionContextChanged;

			if (handler != null)
				handler (this, e);
		}
		
		string ICompletionWidget.GetText (int startOffset, int endOffset)
		{
			string text = editEntry.Text;

			if (startOffset < 0 || endOffset < 0 || startOffset > endOffset || startOffset >= text.Length)
				return "";

			int length = Math.Min (endOffset - startOffset, text.Length - startOffset);

			return text.Substring (startOffset, length);
		}
		
		void ICompletionWidget.Replace (int offset, int count, string text)
		{
			if (count > 0)
				editEntry.Text = editEntry.Text.Remove (offset, count);
			if (!string.IsNullOrEmpty (text))
				editEntry.Text = editEntry.Text.Insert (offset, text);
		}
		
		int ICompletionWidget.CaretOffset {
			get {
				return editEntry.Position;
			}
		}
		
		char ICompletionWidget.GetChar (int offset)
		{
			string txt = editEntry.Text;

			return offset >= txt.Length ? '\0' : txt[offset];
		}
		
		CodeCompletionContext ICompletionWidget.CreateCodeCompletionContext (int triggerOffset)
		{
			var c = new CodeCompletionContext ();
			c.TriggerLine = 0;
			c.TriggerOffset = triggerOffset;
			c.TriggerLineOffset = c.TriggerOffset;
			c.TriggerTextHeight = editEntry.SizeRequest ().Height;
			c.TriggerWordLength = currentCompletionData.ExpressionLength;

			int x, y;
			int tx, ty;
			editEntry.GdkWindow.GetOrigin (out x, out y);
			editEntry.GetLayoutOffsets (out tx, out ty);
			int cp = editEntry.TextIndexToLayoutIndex (editEntry.Position);
			Pango.Rectangle rect = editEntry.Layout.IndexToPos (cp);
			tx += Pango.Units.ToPixels (rect.X) + x;
			y += editEntry.Allocation.Height;

			c.TriggerXCoord = tx;
			c.TriggerYCoord = y;
			return c;
		}
		
		string ICompletionWidget.GetCompletionText (CodeCompletionContext ctx)
		{
			return editEntry.Text.Substring (ctx.TriggerOffset, ctx.TriggerWordLength);
		}
		
		void ICompletionWidget.SetCompletionText (CodeCompletionContext ctx, string partial_word, string complete_word)
		{
			int sp = editEntry.Position - partial_word.Length;
			editEntry.DeleteText (sp, sp + partial_word.Length);
			editEntry.InsertText (complete_word, ref sp);
			editEntry.Position = sp; // sp is incremented by InsertText
		}
		
		void ICompletionWidget.SetCompletionText (CodeCompletionContext ctx, string partial_word, string complete_word, int offset)
		{
			int sp = editEntry.Position - partial_word.Length;
			editEntry.DeleteText (sp, sp + partial_word.Length);
			editEntry.InsertText (complete_word, ref sp);
			editEntry.Position = sp + offset; // sp is incremented by InsertText
		}
		
		int ICompletionWidget.TextLength {
			get {
				return editEntry.Text.Length;
			}
		}
		
		int ICompletionWidget.SelectedLength {
			get {
				return 0;
			}
		}
		
		Style ICompletionWidget.GtkStyle {
			get {
				return editEntry.Style;
			}
		}
		#endregion 

		ObjectValue[] GetValues (string[] names)
		{
			var values = new ObjectValue [names.Length];
			var list = new List<string> ();
			
			for (int n=0; n<names.Length; n++) {
				ObjectValue val;
				if (cachedValues.TryGetValue (names [n], out val))
					values [n] = val;
				else
					list.Add (names[n]);
			}

			ObjectValue[] qvalues;
			if (frame != null)
				qvalues = frame.GetExpressionValues (list.ToArray (), true);
			else {
				qvalues = new ObjectValue [list.Count];
				for (int n=0; n<qvalues.Length; n++)
					qvalues [n] = ObjectValue.CreateUnknown (list [n]);
			}

			int kv = 0;
			for (int n=0; n<values.Length; n++) {
				if (values [n] == null) {
					values [n] = qvalues [kv++];
					cachedValues [names[n]] = values [n];
				}
			}
			
			return values;
		}
		
		Mono.Debugging.Client.CompletionData GetCompletionData (string exp)
		{
			if (CanQueryDebugger && frame != null)
				return frame.GetExpressionCompletionData (exp);

			return null;
		}
		
		internal void SetCustomFont (Pango.FontDescription font)
		{
			crtExp.FontDesc = crtType.FontDesc = crtValue.FontDesc = font;
		}
	}
	
	class DebugCompletionDataList: List<ICSharpCode.NRefactory.Completion.ICompletionData>, ICompletionDataList
	{
		public bool IsSorted { get; set; }
		public DebugCompletionDataList (Mono.Debugging.Client.CompletionData data)
		{
			IsSorted = false;
			foreach (CompletionItem it in data.Items)
				Add (new DebugCompletionData (it));
			AutoSelect =true;
		}
		public bool AutoSelect { get; set; }
		public string DefaultCompletionString {
			get {
				return string.Empty;
			}
		}

		public bool AutoCompleteUniqueMatch {
			get { return false; }
		}
		
		public bool AutoCompleteEmptyMatch {
			get { return false; }
		}
		public bool AutoCompleteEmptyMatchOnCurlyBrace {
			get { return false; }
		}
		public bool CloseOnSquareBrackets {
			get {
				return false;
			}
		}
		
		public CompletionSelectionMode CompletionSelectionMode {
			get; set;
		}

		static readonly List<ICompletionKeyHandler> keyHandler = new List<ICompletionKeyHandler> ();
		public IEnumerable<ICompletionKeyHandler> KeyHandler { get { return keyHandler;} }

		public void OnCompletionListClosed (EventArgs e)
		{
			var handler = CompletionListClosed;

			if (handler != null)
				handler (this, e);
		}
		
		public event EventHandler CompletionListClosed;
	}
	
	class DebugCompletionData : MonoDevelop.Ide.CodeCompletion.CompletionData
	{
		readonly CompletionItem item;
		
		public DebugCompletionData (CompletionItem item)
		{
			this.item = item;
		}
		
		public override IconId Icon {
			get {
				return ObjectValueTreeView.GetIcon (item.Flags);
			}
		}
		
		public override string DisplayText {
			get {
				return item.Name;
			}
		}
		
		public override string CompletionText {
			get {
				return item.Name;
			}
		}
	}
}
