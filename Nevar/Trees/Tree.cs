﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using Nevar.Debugging;
using Nevar.Impl;
using Nevar.Impl.FileHeaders;

namespace Nevar.Trees
{
	public unsafe class Tree
	{
        public long BranchPages;
        public long LeafPages;
        public long OverflowPages;
        public int Depth;
        public long PageCount;

		private readonly SliceComparer _cmp;

		public Page Root;

	    private Tree(SliceComparer cmp, Page root)
		{
			_cmp = cmp;
			Root = root;
		}

        public static Tree Open(Transaction tx, SliceComparer cmp, TreeRootHeader* header)
        {
            var root = tx.GetPage(header->RootPageNumber);
            return new Tree(cmp, root)
                {
                    PageCount = header->PageCount,
                    BranchPages = header->BranchPages,
                    Depth = header->Depth,
                    OverflowPages = header->OverflowPages,
                    LeafPages = header->LeafPages
                };
        }

	    public static Tree Create(Transaction tx, SliceComparer cmp)
		{
			var newRootPage = NewPage(tx, PageFlags.Leaf, 1);
			var tree = new Tree(cmp, newRootPage) { Depth = 1 };
			var cursor = tx.GetCursor(tree);
			cursor.RecordNewPage(newRootPage, 1);
			return tree;
		}

		public void Add(Transaction tx, Slice key, Stream value)
		{
			if (value == null) throw new ArgumentNullException("value");
			if (value.Length > int.MaxValue) throw new ArgumentException("Cannot add a value that is over 2GB in size", "value");
            if(tx.Flags.HasFlag(TransactionFlags.ReadWrite) == false) throw new ArgumentException("Cannot add a value in a read only transaction");

			var cursor = tx.GetCursor(this);

			var page = FindPageFor(tx, key, cursor);

			if (page.LastMatch == 0) // this is an update operation
			{
				RemoveLeafNode(tx, cursor, page);
			}

		    var pageNumber = -1L;
			if (ShouldGoToOverflowPage(value))
			{
				pageNumber = WriteToOverflowPages(tx, cursor, value);
				value = null;
			}

			if (page.HasSpaceFor(key, value) == false)
			{
				new PageSplitter(tx, _cmp, key, value, pageNumber, cursor).Execute();
				DebugValidateTree(tx, cursor.Root);
				return;
			}

			page.AddNode(page.LastSearchPosition, key, value, pageNumber);

			page.DebugValidate(tx, _cmp, cursor.Root);
		}

		private static long WriteToOverflowPages(Transaction tx, Cursor cursor, Stream value)
		{
			var overflowSize = (int)value.Length;
			var numberOfPages = GetNumberOfOverflowPages(overflowSize);
			var overflowPageStart = tx.AllocatePage(numberOfPages);
			overflowPageStart.OverflowSize = numberOfPages;
			overflowPageStart.Flags = PageFlags.Overlfow;
			overflowPageStart.OverflowSize = overflowSize;
			using (var ums = new UnmanagedMemoryStream(overflowPageStart.Base + Constants.PageHeaderSize,
				value.Length, value.Length, FileAccess.ReadWrite))
			{
				value.CopyTo(ums);
			}
			cursor.OverflowPages += numberOfPages;
			cursor.PageCount += numberOfPages;
			return overflowPageStart.PageNumber;
		}

		private static int GetNumberOfOverflowPages(int overflowSize)
		{
			return (Constants.PageSize - 1 + overflowSize) / (Constants.PageSize) + 1;
		}

		private bool ShouldGoToOverflowPage(Stream value)
		{
			return value.Length + Constants.PageHeaderSize > Constants.MaxNodeSize;
		}

		private static void RemoveLeafNode(Transaction tx, Cursor cursor, Page page)
		{
			var node = page.GetNode(page.LastSearchPosition);
			if (node->Flags.HasFlag(NodeFlags.PageRef)) // this is an overflow pointer
			{
				var overflowPage = tx.GetPage(node->PageNumber);
				var numberOfPages = GetNumberOfOverflowPages(overflowPage.OverflowSize);
				for (int i = 0; i < numberOfPages; i++)
				{
					tx.FreePage(tx.GetPage(node->PageNumber + i));
				}
				cursor.OverflowPages -= numberOfPages;
				cursor.PageCount -= numberOfPages;
			}
			page.RemoveNode(page.LastSearchPosition);
		}

		[Conditional("DEBUG")]
		private void DebugValidateTree(Transaction tx, Page root)
		{
			var stack = new Stack<Page>();
			stack.Push(root);
			while (stack.Count > 0)
			{
				var p = stack.Pop();
				p.DebugValidate(tx, _cmp, root);
				if (p.IsBranch == false)
					continue;
				for (int i = 0; i < p.NumberOfEntries; i++)
				{
					stack.Push(tx.GetPage(p.GetNode(i)->PageNumber));
				}
			}
		}



		public Page FindPageFor(Transaction tx, Slice key, Cursor cursor)
		{
			var p = cursor.Root;
			cursor.Push(p);
			while (p.Flags.HasFlag(PageFlags.Branch))
			{
				int nodePos;
				if (key.Options == SliceOptions.BeforeAllKeys)
				{
					p.LastSearchPosition = nodePos = 0;
				}
				else if (key.Options == SliceOptions.AfterAllKeys)
				{
					p.LastSearchPosition  = nodePos = (ushort)(p.NumberOfEntries - 1);
				}
				else
				{
					if (p.Search(key, _cmp) != null)
					{
						nodePos = p.LastSearchPosition;
						if (p.LastMatch != 0)
						{
							nodePos--;
							p.LastSearchPosition--;
						}
					}
					else
					{
						nodePos = (ushort)(p.LastSearchPosition - 1);
					}

				}

				var node = p.GetNode(nodePos);
				p = tx.GetPage(node->PageNumber);
				cursor.Push(p);
			}

			if (p.IsLeaf == false)
				throw new DataException("Index points to a non leaf page");

			p.NodePositionFor(key, _cmp); // will set the LastSearchPosition

			return p;
		}

		internal static Page NewPage(Transaction tx, PageFlags flags, int num)
		{
			var page = tx.AllocatePage(num);

			page.Flags = flags;

			return page;
		}

		public void Delete(Transaction tx, Slice key)
		{
            if (tx.Flags.HasFlag(TransactionFlags.ReadWrite) == false) throw new ArgumentException("Cannot delete a value in a read only transaction");

			var cursor = tx.GetCursor(this);

			var page = FindPageFor(tx, key, cursor);

			page.NodePositionFor(key, _cmp);
			if (page.LastMatch != 0)
				return; // not an exact match, can't delete
			RemoveLeafNode(tx, cursor, page);

			var treeRebalancer = new TreeRebalancer(tx, _cmp);

			var changedPage = page;
			while (changedPage != null)
			{
				changedPage = treeRebalancer.Execute(cursor, changedPage);
			}

			page.DebugValidate(tx, _cmp, cursor.Root);
		}

		public List<Slice> KeysAsList(Transaction tx)
		{
			var l = new List<Slice>();
			AddKeysToListInOrder(tx, l, Root);
			return l;
		}

		private void AddKeysToListInOrder(Transaction tx, List<Slice> l, Page page)
		{
			for (int i = 0; i < page.NumberOfEntries; i++)
			{
				var node = page.GetNode(i);
				if (page.IsBranch)
				{
					var p = tx.GetPage(node->PageNumber);
					AddKeysToListInOrder(tx, l, p);
				}
				else
				{
					l.Add(new Slice(node));
				}
			}
		}

		public Iterator Iterage(Transaction tx)
		{
			return new Iterator(this, tx, _cmp);
		}

		public Stream Read(Transaction tx, Slice key)
		{
			var cursor = tx.GetCursor(this);
			var p = FindPageFor(tx, key, cursor);
			var node = p.Search(key, _cmp);

			if (node == null)
				return null;

			var item1 = new Slice(node);

			if (item1.Compare(key, _cmp) != 0)
				return null;
			return StreamForNode(tx, node);
		}

		internal static Stream StreamForNode(Transaction tx, NodeHeader* node)
		{
			if (node->Flags.HasFlag(NodeFlags.PageRef))
			{
				var overFlowPage = tx.GetPage(node->PageNumber);
				return new UnmanagedMemoryStream(overFlowPage.Base + Constants.PageHeaderSize, overFlowPage.OverflowSize,
				                                 overFlowPage.OverflowSize, FileAccess.Read);
			}
			return new UnmanagedMemoryStream((byte*) node + node->KeySize + Constants.NodeHeaderSize, node->DataSize,
			                                 node->DataSize, FileAccess.Read);
		}

	    public void CopyTo(TreeRootHeader* header)
	    {
	        header->BranchPages = BranchPages;
	        header->Depth = Depth;
            header->Flags = TreeFlags.None;
	        header->LeafPages = LeafPages;
	        header->OverflowPages = OverflowPages;
	        header->PageCount = PageCount;
	        header->RootPageNumber = Root.PageNumber;
	    }
	}
}