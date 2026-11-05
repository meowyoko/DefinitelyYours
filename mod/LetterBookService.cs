using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace AiPhone
{
    internal static class LetterBookService
    {
        internal const string BookItemId = "ai_phone.letters_book";
        private const string ModId = "ai_phone";
        private const string LetterIdTag = "AI_PHONE_LETTER_ID";
        private const string LetterStoredTag = "AI_PHONE_LETTER_STORED";

        private static ModItemDirectory directory;
        private static GameItem openedLetter;

        internal static void Register(ModItemDirectory modDirectory)
        {
            if (modDirectory == null || ReferenceEquals(directory, modDirectory))
            {
                return;
            }

            directory = modDirectory;
            bool added = directory.Add(BookItemId, CreateDefinition);
            Prototype.Log?.Log(added
                ? "已注册实体来信册 ai_phone.letters_book（内部 6×8，仅收 Definitely Yours 字条）。"
                : "实体来信册 ai_phone.letters_book 已存在，跳过重复注册。" );
        }

        internal static void OnGameLoaded()
        {
            openedLetter = null;
            if (!PlayerStore.HasInstance)
            {
                return;
            }

            List<GameItem> books = FindBooks();
            foreach (GameItem book in books)
            {
                ConfigureBookItem(book);
                ModHelper.SetItemOwnerShip(book, true);
            }

            if (books.Count > 1)
            {
                Prototype.Log?.Error($"检测到 {books.Count} 本来信册；为避免丢失实体字条，本次不自动销毁重复册子。" );
            }
        }

        internal static void MarkParcelNote(GameItem handnote)
        {
            if (handnote == null)
            {
                return;
            }

            string id = Guid.NewGuid().ToString("N");
            handnote.ModifyTag(LetterIdTag, tag =>
            {
                tag.SetString(id);
                tag.Enable();
            }, false);
        }

        internal static void TrackOpenedLetter(GameItem handnote)
        {
            openedLetter = null;
            if (!IsLetter(handnote) || handnote.IsTag(LetterStoredTag))
            {
                return;
            }

            // 兼容 1.5.0 生成的字条：当时写入了字符串，但没有启用标签。
            handnote.ModifyTag(LetterIdTag, tag => tag.Enable(), false);
            openedLetter = handnote;
            Prototype.Log?.Log("已阅读 Definitely Yours 字条；关闭阅读界面后尝试收进来信册。" );
        }

        internal static void HandleLetterClosed()
        {
            GameItem letter = openedLetter;
            openedLetter = null;
            if (!IsLetter(letter))
            {
                return;
            }

            GameItem book = FindUsableBook();
            if (book == null)
            {
                book = CreateAndPlaceBook();
                if (book == null)
                {
                    Prototype.Log?.Error("来信册无处投放；字条保留在原位。" );
                    return;
                }
            }

            GameInventory inventory = BookInventory(book);
            if (inventory == null)
            {
                return;
            }

            if (ReferenceEquals(letter.parentInventory, inventory))
            {
                MarkStored(letter);
                return;
            }

            if (inventory.TryFindOneValidInventorySlot(letter) == null)
            {
                Prototype.Log?.Log("来信册已满；字条保留在原位。" );
                return;
            }

            GameInventory previous = letter.parentInventory;
            inventory.TryAcceptAll(letter);
            if (!ReferenceEquals(letter.parentInventory, inventory))
            {
                Prototype.Log?.Error("字条未能放进来信册，已保留在原位。" );
                return;
            }

            MarkStored(letter);
            Prototype.Log?.Log($"字条已从 {(previous == null ? "原位置" : "原容器")} 收进来信册。" );
        }

        internal static void ConfigureBookItem(GameItem book)
        {
            if (book == null || !string.Equals(book.identifier, BookItemId, StringComparison.Ordinal))
            {
                return;
            }

            book.name = $"{Prototype.AiName} 的来信";
            book.shortDescription = $"收着 {Prototype.AiName} 寄来的字条。";
            book.unitValue = 0;
            book.EnableTag(GeneralHelper.IMPORTANT_TAG, false);
            book.EnableTag(ContainerHelper.CONTAINER_TAG, false);
            book.SetGameItemType(TypeHelper.DOCUMENT);
            book.SetGameItemType(TypeHelper.NOT_FOR_RESALE);
            book.SetShape(new GridShapeBuilder().SetData(new byte[] { 1, 1, 1, 1 }, 2));
        }

        private static GameItem CreateDefinition()
        {
            var created = DirectoryUtils.CreateInventoryWindow(6, 8, true);
            GameItem book = directory.Empty()
                .SetContentWindow(created.Item1)
                .SetSpriteAndShapeFromMod(ModId, "letters_book");
            ConfigureBookItem(book);
            ContainerHelper.InitContainerItemIncludeContainer(created.Item2, book);
            ContainerHelper.AllowOnlyTaggedItem(created.Item2, LetterIdTag);
            ModHelper.SetSource(book, ModId, "Definitely Yours");
            return book;
        }

        private static GameItem CreateAndPlaceBook()
        {
            GameItem book = DirectoryMaster.Item(BookItemId, true);
            if (book == null)
            {
                Prototype.Log?.Error("无法从物品目录生成来信册。" );
                return null;
            }

            ConfigureBookItem(book);
            ModHelper.SetItemOwnerShip(book, true);
            string placed = ParcelService.TryPlaceItem(book);
            if (placed == "failed")
            {
                book.Destroy();
                return null;
            }

            Prototype.Log?.Log($"已生成来信册并放入 {placed}。" );
            return book;
        }

        private static GameItem FindUsableBook()
        {
            GameItem book = FindBooks().FirstOrDefault(candidate => BookInventory(candidate) != null);
            if (book != null)
            {
                ConfigureBookItem(book);
                ModHelper.SetItemOwnerShip(book, true);
            }

            return book;
        }

        private static List<GameItem> FindBooks()
        {
            // false 表示不按所有权过滤；这样旧版本留下的“未拥有”册子也能被找到并修正。
            return PlayerStore.Instance.FindAllItem(false)
                .Where(item => item != null
                    && string.Equals(item.identifier, BookItemId, StringComparison.Ordinal))
                .Distinct()
                .ToList();
        }

        private static GameInventory BookInventory(GameItem book)
        {
            return book?.contentWindow?.child as GameInventory;
        }

        private static bool IsLetter(GameItem item)
        {
            if (item == null)
            {
                return false;
            }

            try
            {
                return !string.IsNullOrWhiteSpace(item.GetTagReadonly(LetterIdTag).GetString());
            }
            catch
            {
                return false;
            }
        }

        private static void MarkStored(GameItem letter)
        {
            letter.ModifyTag(LetterStoredTag, tag => tag.Enable(), false);
        }
    }

    [HarmonyPatch(typeof(HandnoteUIManager), nameof(HandnoteUIManager.OpenPanel))]
    internal static class TrackAiPhoneLetterPatch
    {
        private static void Postfix(GameItem gameItem)
        {
            LetterBookService.TrackOpenedLetter(gameItem);
        }
    }

    [HarmonyPatch(typeof(HandnoteUIManager), nameof(HandnoteUIManager.ClosePanel))]
    internal static class StoreAiPhoneLetterPatch
    {
        private static void Postfix()
        {
            LetterBookService.HandleLetterClosed();
        }
    }
}
