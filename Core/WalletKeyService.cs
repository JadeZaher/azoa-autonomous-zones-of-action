using System.Security.Cryptography;

namespace AZOA.WebAPI.Core;

/// <summary>
/// Generates cryptographic keypairs for supported chains and encrypts private keys
/// for secure storage. Uses AES-256-GCM with a derived key from an app secret.
/// </summary>
public class WalletKeyService
{
    private readonly byte[] _encryptionKey;

    public WalletKeyService(IConfiguration config)
    {
        var secret = config.GetValue<string>("AZOA:WalletEncryptionKey")
                     ?? throw new InvalidOperationException(
                         "AZOA:WalletEncryptionKey is required for platform wallet generation.");

        // Derive a 256-bit key from the secret using SHA-256
        _encryptionKey = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret));
    }

    /// <summary>
    /// Generate a keypair for the given chain type.
    /// Returns (publicKey, privateKeyHex, address, seedPhrase).
    /// </summary>
    public (string publicKey, string privateKeyHex, string address, string? seedPhrase) GenerateKeypair(string chainType)
    {
        return chainType.ToLowerInvariant() switch
        {
            "algorand" => GenerateAlgorandKeypair(),
            "solana" => GenerateSolanaKeypair(),
            "ethereum" => GenerateEthereumKeypair(),
            _ => throw new NotSupportedException($"Chain type '{chainType}' is not supported for wallet generation.")
        };
    }

    /// <summary>Encrypt a private key hex string for storage.</summary>
    public string EncryptPrivateKey(string privateKeyHex)
    {
        return AesGcmEncrypt(privateKeyHex);
    }

    /// <summary>Decrypt a stored encrypted private key.</summary>
    public string DecryptPrivateKey(string encryptedHex)
    {
        return AesGcmDecrypt(encryptedHex);
    }

    /// <summary>Encrypt a seed phrase for storage.</summary>
    public string EncryptSeedPhrase(string seedPhrase)
    {
        return AesGcmEncrypt(seedPhrase);
    }

    /// <summary>Decrypt a stored encrypted seed phrase.</summary>
    public string DecryptSeedPhrase(string encryptedHex)
    {
        return AesGcmDecrypt(encryptedHex);
    }

    // ─── Chain-specific generation ───

    private (string, string, string, string?) GenerateAlgorandKeypair()
    {
        // Real Ed25519 keypair via the already-referenced Algorand2 package
        // (signing-core-keystone B1). Algorand2 owns the canonical
        // address/checksum/mnemonic rules — no hand-rolled curve math.
        var account = new Algorand.Algod.Model.Account();

        // The private key we persist is Algorand2's clear-text Ed25519 private key
        // bytes. AlgorandTransactionSigner reconstructs the signing account via
        // `new Account(privateKeyBytes)`, so keygen and signing share this exact
        // representation — the round-trip test pins that invariant.
        var privateKeyBytes = account.KeyPair.ClearTextPrivateKey;
        var publicKeyBytes = account.KeyPair.ClearTextPublicKey;

        var privateKeyHex = AZOA.WebAPI.Helpers.Encoding.ToLowerHex(privateKeyBytes);
        var publicKeyHex = AZOA.WebAPI.Helpers.Encoding.ToLowerHex(publicKeyBytes);
        var address = account.Address.EncodeAsString();   // real SHA-512/256 checksum + base32
        var seedPhrase = account.ToMnemonic();            // real, restorable 25-word mnemonic

        return (publicKeyHex, privateKeyHex, address, seedPhrase);
    }

    private (string, string, string, string?) GenerateSolanaKeypair()
    {
        // Solana also uses Ed25519
        var seed = RandomNumberGenerator.GetBytes(32);
        var privateKeyBytes = new byte[64]; // Solana private key is the full keypair
        var publicKey = DeriveEd25519PublicKey(seed);
        Array.Copy(seed, 0, privateKeyBytes, 0, 32);
        Array.Copy(publicKey, 0, privateKeyBytes, 32, 32);

        var privateKeyHex = AZOA.WebAPI.Helpers.Encoding.ToLowerHex(privateKeyBytes);
        // Solana address is the base58-encoded public key
        var address = Base58Encode(publicKey);
        var seedPhrase = GenerateMnemonic(seed);

        return (AZOA.WebAPI.Helpers.Encoding.ToLowerHex(publicKey), privateKeyHex, address, seedPhrase);
    }

    private (string, string, string, string?) GenerateEthereumKeypair()
    {
        // Ethereum uses secp256k1
        var privateKeyBytes = RandomNumberGenerator.GetBytes(32);
        var privateKeyHex = AZOA.WebAPI.Helpers.Encoding.ToLowerHex(privateKeyBytes);
        var publicKey = DeriveSecp256k1PublicKey(privateKeyBytes);
        // Ethereum address is the last 20 bytes of keccak256(publicKey)
        var address = EthereumAddressFromPublicKey(publicKey);
        var seedPhrase = GenerateMnemonic(privateKeyBytes);

        return (AZOA.WebAPI.Helpers.Encoding.ToLowerHex(publicKey), privateKeyHex, address, seedPhrase);
    }

    // ─── Ed25519 public key derivation (simplified) ───

    private byte[] DeriveEd25519PublicKey(byte[] seed)
    {
        // DEPLOY-STUB (H1, see conductor/DEPLOY-STEPS-TODO.md): Solana keygen is
        // still a placeholder. Algorand now routes through Algorand2 (real
        // Ed25519); this HMAC derivation remains ONLY for the not-yet-real Solana
        // path and must be replaced with the Solana SDK keypair before Solana
        // value flow is enabled.
        // In production, use a proper Ed25519 implementation like libsodium or BouncyCastle.
        // For now, we use HMAC-SHA512 to derive a clamped scalar and then multiply.
        // This is a placeholder that matches the key format — real Ed25519 requires
        // proper scalar multiplication on Curve25519.
        //
        // We use SHA-512 to derive the seed as done in Ed25519:
        using var hmac = new HMACSHA512(seed);
        var hash = hmac.ComputeHash("ed25519 seed"u8.ToArray());
        var publicKey = new byte[32];
        Array.Copy(hash, 32, publicKey, 0, 32);
        return publicKey;
    }

    // ─── Secp256k1 public key derivation (simplified) ───

    private byte[] DeriveSecp256k1PublicKey(byte[] privateKey)
    {
        // DEPLOY-STUB (H1, see conductor/DEPLOY-STEPS-TODO.md): Ethereum keygen is
        // still a placeholder (HMAC, not secp256k1). Out of scope for the
        // Algorand-first signing-core-keystone track; replace before Ethereum
        // value flow is enabled.
        // In production, use a proper secp256k1 library.
        // Placeholder that generates a 64-byte uncompressed public key
        using var hmac = new HMACSHA256(privateKey);
        var hash = hmac.ComputeHash("secp256k1"u8.ToArray());
        var publicKey = new byte[64];
        Array.Copy(hash, 0, publicKey, 0, 64);
        return publicKey;
    }

    // ─── Algorand address from public key ───

    private string AlgorandAddressFromPublicKey(byte[] publicKey)
    {
        // Algorand address = base32(publicKey + checksum)
        // Checksum = first 4 bytes of SHA-512/256(publicKey)
        using var sha = SHA512.Create();
        var fullHash = sha.ComputeHash(publicKey);
        var checksum = new byte[4];
        Array.Copy(fullHash, 0, checksum, 0, 4);

        var combined = new byte[publicKey.Length + 4];
        Array.Copy(publicKey, combined, publicKey.Length);
        Array.Copy(checksum, 0, combined, publicKey.Length, 4);

        return Base32Encode(combined);
    }

    // ─── Ethereum address ───

    private string EthereumAddressFromPublicKey(byte[] publicKey)
    {
        // Ethereum address = "0x" + last 20 bytes of keccak256(publicKey)
        // For now, use SHA-256 as a fallback (in production, use keccak256)
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(publicKey);
        var addressBytes = new byte[20];
        Array.Copy(hash, hash.Length - 20, addressBytes, 0, 20);

        return "0x" + AZOA.WebAPI.Helpers.Encoding.ToLowerHex(addressBytes);
    }

    // ─── AES-256-GCM encryption ───

    private string AesGcmEncrypt(string plaintext)
    {
        var plaintextBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(12); // 96-bit nonce for GCM
        var tag = new byte[16];
        var ciphertext = new byte[plaintextBytes.Length];

        using var aes = new AesGcm(_encryptionKey, 16);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        // Pack as: nonce (12) + tag (16) + ciphertext
        var result = new byte[12 + 16 + ciphertext.Length];
        Array.Copy(nonce, 0, result, 0, 12);
        Array.Copy(tag, 0, result, 12, 16);
        Array.Copy(ciphertext, 0, result, 28, ciphertext.Length);

        return AZOA.WebAPI.Helpers.Encoding.ToLowerHex(result);
    }

    private string AesGcmDecrypt(string encryptedHex)
    {
        var data = Convert.FromHexString(encryptedHex);
        if (data.Length < 28)
            throw new InvalidOperationException("Invalid encrypted data format.");

        var nonce = data[..12];
        var tag = data[12..28];
        var ciphertext = data[28..];

        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(_encryptionKey, 16);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return System.Text.Encoding.UTF8.GetString(plaintext);
    }

    // ─── Mnemonic generation (simplified BIP39-like) ───

    private static readonly string[] Bip39Wordlist =
    {
        "abandon", "ability", "able", "about", "above", "absent", "absorb", "abstract",
        "absurd", "abuse", "access", "accident", "account", "accuse", "achieve", "acid",
        "acoustic", "acquire", "across", "act", "action", "actor", "actress", "actual",
        "adapt", "add", "addict", "address", "adjust", "admit", "adult", "advance",
        "advice", "aerobic", "affair", "afford", "afraid", "again", "age", "agent",
        "agree", "ahead", "aim", "air", "airport", "aisle", "alarm", "album",
        "alcohol", "alert", "alien", "all", "alley", "allow", "almost", "alone",
        "alpha", "already", "also", "alter", "always", "amateur", "amazing", "among",
        "amount", "amused", "analyst", "anchor", "ancient", "anger", "angle", "angry",
        "animal", "ankle", "announce", "annual", "another", "answer", "antenna", "antique",
        "anxiety", "any", "apart", "apology", "appear", "apple", "approve", "april",
        "arch", "arctic", "area", "arena", "argue", "arm", "armed", "armor",
        "army", "around", "arrange", "arrest", "arrive", "arrow", "art", "artefact",
        "artist", "artwork", "ask", "aspect", "assault", "asset", "assist", "assume",
        "asthma", "athlete", "atom", "attack", "attend", "attitude", "attract", "auction",
        "audit", "august", "aunt", "author", "auto", "autumn", "average", "avocado",
        "avoid", "awake", "aware", "away", "awesome", "awful", "awkward", "axis",
        "baby", "bachelor", "bacon", "badge", "bag", "balance", "balcony", "ball",
        "bamboo", "banana", "banner", "bar", "barely", "bargain", "barrel", "base",
        "basic", "basket", "battle", "beach", "bean", "beauty", "because", "become",
        "beef", "before", "begin", "behave", "behind", "believe", "below", "belt",
        "bench", "benefit", "best", "betray", "better", "between", "beyond", "bicycle",
        "bid", "bike", "bind", "biology", "bird", "birth", "bitter", "black",
        "blade", "blame", "blanket", "blast", "bleak", "bless", "blind", "blood",
        "blossom", "blouse", "blue", "blur", "blush", "board", "boat", "body",
        "boil", "bomb", "bone", "bonus", "book", "boost", "border", "boring",
        "borrow", "boss", "bottom", "bounce", "box", "boy", "bracket", "brain",
        "brand", "brass", "brave", "bread", "breeze", "brick", "bridge", "brief",
        "bright", "bring", "brisk", "broccoli", "broken", "bronze", "broom", "brother",
        "brown", "brush", "bubble", "buddy", "budget", "buffalo", "build", "bulb",
        "bulk", "bullet", "bundle", "bunker", "burden", "burger", "burst", "bus",
        "business", "busy", "butter", "buyer", "buzz", "cabbage", "cabin", "cable",
        "cactus", "cage", "cake", "call", "calm", "camera", "camp", "can",
        "canal", "cancel", "candy", "cannon", "canoe", "canvas", "canyon", "capable",
        "capital", "captain", "car", "carbon", "card", "cargo", "carpet", "carry",
        "cart", "case", "cash", "casino", "castle", "casual", "cat", "catalog",
        "catch", "category", "cattle", "caught", "cause", "caution", "cave", "ceiling",
        "celery", "cement", "census", "century", "cement", "ceo", "ceremony", "certain",
        "chair", "chalk", "champion", "change", "chaos", "chapter", "charge", "chase",
        "chat", "cheap", "check", "cheese", "chef", "cherry", "chest", "chicken",
        "chief", "child", "chimney", "choice", "choose", "chronic", "chuckle", "chunk",
        "churn", "cigar", "cinnamon", "circle", "citizen", "city", "civil", "claim",
        "clap", "clarify", "claw", "clay", "clean", "clerk", "clever", "click",
        "client", "cliff", "climb", "clinic", "clip", "clock", "clog", "close",
        "cloth", "cloud", "clown", "club", "clump", "cluster", "clutch", "coach",
        "coast", "coconut", "code", "coffee", "coil", "coin", "collect", "color",
        "column", "combine", "come", "comfort", "comic", "common", "company", "concert",
        "conduct", "confirm", "congress", "connect", "consider", "control", "convince",
        "cook", "cool", "copper", "copy", "coral", "core", "corn", "correct",
        "cost", "cotton", "couch", "country", "couple", "course", "cousin", "cover",
        "coyote", "crack", "cradle", "craft", "cram", "crane", "crash", "crater",
        "crawl", "crazy", "cream", "credit", "creek", "crew", "cricket", "crime",
        "crisp", "critic", "crop", "cross", "crouch", "crowd", "crucial", "cruel",
        "cruise", "crumble", "crunch", "crush", "cry", "crystal", "cube", "culture",
        "cup", "cupboard", "curious", "current", "curtain", "curve", "cushion", "custom",
        "cute", "cycle", "dad", "damage", "damp", "dance", "danger", "daring",
        "dash", "daughter", "dawn", "day", "deal", "debate", "debris", "decade",
        "december", "decide", "decline", "decorate", "decrease", "deer", "defense",
        "define", "defy", "degree", "delay", "deliver", "demand", "demise", "denial",
        "dentist", "deny", "depart", "depend", "deposit", "depth", "deputy", "derive",
        "describe", "desert", "design", "desk", "despair", "destroy", "detail", "detect",
        "develop", "device", "devote", "diagram", "dial", "diamond", "diary", "dice",
        "diesel", "diet", "differ", "digital", "dignity", "dilemma", "dinner", "dinosaur",
        "direct", "dirt", "disagree", "discover", "disease", "dish", "dismiss", "disorder",
        "display", "distance", "divert", "divide", "divorce", "dizzy", "doctor", "document",
        "dog", "doll", "dolphin", "domain", "donate", "donkey", "donor", "door",
        "dose", "double", "dove", "draft", "dragon", "drama", "drastic", "draw",
        "dream", "dress", "drift", "drill", "drink", "drip", "drive", "drop",
        "drum", "dry", "duck", "dumb", "dune", "during", "dust", "dutch",
        "duty", "dwarf", "dynamic", "eager", "eagle", "early", "earn", "earth",
        "easily", "east", "easy", "echo", "ecology", "economy", "edge", "edit",
        "educate", "effort", "egg", "eight", "either", "elbow", "elder", "electric",
        "elegant", "element", "elephant", "elevator", "elite", "else", "embark", "embody",
        "embrace", "emerge", "emotion", "employ", "empower", "empty", "enable", "enact",
        "end", "endless", "endorse", "enemy", "energy", "enforce", "engage", "engine",
        "enhance", "enjoy", "enlist", "enough", "enrich", "enroll", "ensure", "enter",
        "entire", "entry", "envelope", "episode", "equal", "equip", "era", "erase",
        "erode", "erosion", "error", "erupt", "escape", "essay", "essence", "estate",
        "eternal", "ethics", "evidence", "evil", "evoke", "evolve", "exact", "example",
        "excess", "exchange", "excite", "exclude", "excuse", "execute", "exercise",
        "exhaust", "exhibit", "exile", "exist", "exit", "exotic", "expand", "expect",
        "expire", "explain", "expose", "express", "extend", "extra", "eye", "eyebrow",
        "fabric", "face", "faculty", "fade", "faint", "faith", "fall", "false",
        "fame", "family", "famous", "fan", "fancy", "fantasy", "farm", "fashion",
        "fat", "fatal", "father", "fatigue", "fault", "favorite", "feature", "february",
        "federal", "fee", "feed", "feel", "female", "fence", "festival", "fetch",
        "fever", "few", "fiber", "fiction", "field", "figure", "file", "film",
        "filter", "final", "find", "fine", "finger", "finish", "fire", "firm",
        "first", "fiscal", "fish", "fit", "fitness", "fix", "flag", "flame",
        "flash", "flat", "flavor", "flee", "flight", "flip", "float", "flock",
        "floor", "flower", "fluid", "flush", "fly", "foam", "focus", "fog",
        "foil", "fold", "follow", "food", "foot", "force", "foreign", "forest",
        "forget", "fork", "fortune", "forum", "forward", "fossil", "foster", "found",
        "fox", "fragile", "frame", "frequent", "fresh", "friend", "fringe", "frog",
        "front", "frost", "frown", "frozen", "fruit", "fuel", "fun", "funny",
        "furnace", "fury", "future", "gadget", "gain", "galaxy", "gallery", "game",
        "gap", "garage", "garbage", "garden", "garlic", "garment", "gas", "gasp",
        "gate", "gather", "gauge", "gaze", "general", "genius", "genre", "gentle",
        "genuine", "gesture", "ghost", "giant", "gift", "giggle", "ginger", "giraffe",
        "girl", "give", "glad", "glance", "glare", "glass", "glide", "glimpse",
        "globe", "gloom", "glory", "glove", "glow", "glue", "goat", "goddess",
        "gold", "good", "goose", "gorilla", "gospel", "gossip", "govern", "gown",
        "grab", "grace", "grain", "grant", "grape", "grass", "gravity", "great",
        "green", "grid", "grief", "grit", "grocery", "group", "grow", "grunt",
        "guard", "guess", "guide", "guilt", "guitar", "gun", "gym", "habit",
        "hair", "half", "hammer", "hamster", "hand", "happy", "harbor", "hard",
        "harsh", "harvest", "hat", "have", "hawk", "hazard", "head", "health",
        "heart", "heavy", "hedgehog", "height", "hello", "helmet", "help", "hen",
        "hero", "hidden", "high", "hill", "hint", "hip", "hire", "history",
        "hobby", "hockey", "hold", "hole", "holiday", "hollow", "home", "honey",
        "hood", "hope", "horn", "horror", "horse", "hospital", "host", "hotel",
        "hour", "hover", "hub", "huge", "human", "humble", "humor", "hundred",
        "hungry", "hunt", "hurdle", "hurry", "hurt", "husband", "hybrid", "ice",
        "icon", "idea", "identify", "idle", "ignore", "ill", "illegal", "illness",
        "image", "imitate", "immense", "immune", "impact", "impose", "improve", "impulse",
        "inch", "include", "income", "increase", "index", "indicate", "indoor", "industry",
        "infant", "inflict", "inform", "inhale", "inherit", "initial", "inject", "injury",
        "inmate", "inner", "innocent", "input", "inquiry", "insane", "insect", "inside",
        "inspire", "install", "intact", "interest", "into", "invest", "invite", "involve",
        "iron", "island", "isolate", "issue", "item", "ivory", "jacket", "jaguar",
        "jar", "jazz", "jealous", "jeans", "jelly", "jewel", "job", "join",
        "joke", "journey", "joy", "judge", "juice", "jump", "jungle", "junior",
        "junk", "just", "kangaroo", "keen", "keep", "ketchup", "key", "kick",
        "kid", "kidney", "kind", "kingdom", "kiss", "kit", "kitchen", "kite",
        "kitten", "kiwi", "knee", "knife", "knock", "know", "lab", "label",
        "labor", "ladder", "lady", "lake", "lamp", "language", "laptop", "large",
        "later", "latin", "laugh", "laundry", "lava", "law", "lawn", "lawsuit",
        "layer", "lazy", "leader", "leaf", "learn", "leave", "lecture", "left",
        "leg", "legal", "legend", "leisure", "lemon", "lend", "length", "lens",
        "leopard", "lesson", "letter", "level", "liar", "liberty", "library", "license",
        "life", "lift", "light", "like", "limb", "limit", "link", "lion",
        "liquid", "list", "little", "live", "lizard", "load", "loan", "lobster",
        "local", "lock", "logic", "lonely", "long", "loop", "lottery", "loud",
        "lounge", "love", "loyal", "lucky", "luggage", "lumber", "lunar", "lunch",
        "luxury", "lyrics", "machine", "mad", "magic", "magnet", "maid", "mail",
        "main", "major", "make", "mammal", "man", "manage", "mandate", "mango",
        "mansion", "manual", "maple", "marble", "march", "margin", "marine", "market",
        "marriage", "mask", "mass", "master", "match", "material", "math", "matrix",
        "matter", "maximum", "maze", "meadow", "mean", "measure", "meat", "mechanic",
        "medal", "media", "melody", "melt", "member", "memory", "mention", "menu",
        "mercy", "merge", "merit", "merry", "mesh", "message", "metal", "method",
        "middle", "midnight", "milk", "million", "mimic", "mind", "minimum", "minor",
        "minute", "miracle", "mirror", "misery", "miss", "mistake", "mix", "mixed",
        "mixture", "mobile", "model", "modify", "mom", "moment", "monitor", "monkey",
        "monster", "month", "moon", "moral", "more", "morning", "mosquito", "mother",
        "motion", "motor", "mountain", "mouse", "move", "movie", "much", "muffin",
        "mule", "multiply", "muscle", "museum", "mushroom", "music", "must", "mutual",
        "myself", "mystery", "myth", "naive", "name", "napkin", "narrow", "nasty",
        "nation", "nature", "near", "neck", "need", "negative", "neglect", "neither",
        "nephew", "nerve", "nest", "net", "network", "neutral", "never", "news",
        "next", "nice", "night", "noble", "noise", "nominee", "noodle", "normal",
        "north", "nose", "notable", "note", "nothing", "notice", "novel", "now",
        "nuclear", "number", "nurse", "nut", "oak", "obey", "object", "oblige",
        "obscure", "observe", "obtain", "obvious", "occur", "ocean", "october", "odor",
        "off", "offer", "office", "often", "oil", "okay", "old", "olive",
        "olympic", "omit", "once", "one", "onion", "online", "only", "open",
        "opera", "opinion", "oppose", "option", "orange", "orbit", "orchard", "order",
        "ordinary", "organ", "orient", "original", "orphan", "ostrich", "other", "outdoor",
        "outer", "output", "outside", "oval", "oven", "over", "own", "owner",
        "oxygen", "oyster", "ozone", "pact", "paddle", "page", "pair", "palace",
        "palm", "panda", "panel", "panic", "panther", "paper", "parade", "parent",
        "park", "parrot", "party", "pass", "patch", "path", "patient", "patrol",
        "pattern", "pause", "pave", "payment", "peace", "peanut", "pear", "peasant",
        "pelican", "pen", "penalty", "pencil", "people", "pepper", "perfect", "permit",
        "person", "pet", "phone", "photo", "phrase", "physical", "piano", "picnic",
        "picture", "piece", "pig", "pigeon", "pill", "pilot", "pink", "pioneer",
        "pipe", "pistol", "pitch", "pizza", "place", "planet", "plastic", "plate",
        "play", "player", "please", "pledge", "pluck", "plug", "plunge", "poem",
        "poet", "point", "polar", "pole", "police", "pond", "pony", "pool",
        "popular", "portion", "position", "possible", "post", "potato", "pottery", "poverty",
        "powder", "power", "practice", "praise", "predict", "prefer", "prepare", "present",
        "pretty", "prevent", "price", "pride", "primary", "print", "priority", "prison",
        "private", "prize", "problem", "process", "produce", "profit", "program", "project",
        "property", "proposal", "protect", "prove", "provide", "public", "pudding", "pull",
        "pulp", "pulse", "pumpkin", "punch", "pupil", "puppy", "purchase", "purity",
        "purpose", "purse", "push", "put", "puzzle", "pyramid", "quality", "quantum",
        "quarter", "question", "quick", "quit", "quiz", "quote", "rabbit", "raccoon",
        "race", "rack", "radar", "radio", "rail", "rain", "raise", "rally",
        "ramp", "ranch", "random", "range", "rapid", "rare", "rate", "rather",
        "raven", "raw", "razor", "ready", "real", "reason", "rebel", "rebuild",
        "recall", "receive", "recipe", "record", "recycle", "reduce", "reflect", "reform",
        "refuse", "region", "regret", "regular", "reject", "relax", "release", "relief",
        "rely", "remain", "remember", "remind", "remove", "render", "renew", "rent",
        "reopen", "repair", "repeat", "replace", "report", "require", "rescue", "resemble",
        "resist", "resource", "response", "result", "retire", "retreat", "return", "reunion",
        "reveal", "review", "reward", "rhythm", "rib", "ribbon", "rice", "rich",
        "ride", "ridge", "rifle", "right", "rigid", "ring", "riot", "ripple",
        "risk", "ritual", "rival", "river", "road", "roast", "robot", "robust",
        "rocket", "romance", "roof", "rookie", "room", "rose", "rotate", "rough",
        "round", "route", "royal", "rubber", "rude", "rug", "rule", "run",
        "runway", "rural", "sad", "saddle", "sadness", "safe", "sail", "salad",
        "salmon", "salon", "salt", "salute", "same", "sample", "sand", "satisfy",
        "satoshi", "sauce", "sausage", "save", "scale", "scan", "scare", "scatter",
        "scene", "scheme", "school", "science", "scissors", "scorpion", "scout", "scrap",
        "screen", "script", "scrub", "sea", "search", "season", "seat", "second",
        "secret", "section", "security", "seed", "seek", "segment", "select", "sell",
        "seminar", "senior", "sense", "sentence", "series", "service", "session", "settle",
        "setup", "seven", "shadow", "shaft", "shallow", "share", "shed", "shell",
        "sheriff", "shield", "shift", "shine", "ship", "shiver", "shock", "shoe",
        "shoot", "shop", "short", "shoulder", "shove", "shrimp", "shrug", "shuffle",
        "shy", "sibling", "sick", "side", "siege", "sight", "sign", "silent",
        "silk", "silly", "silver", "similar", "simple", "since", "sing", "siren",
        "sister", "situation", "six", "size", "skate", "sketch", "ski", "skill",
        "skin", "skirt", "skull", "slab", "slam", "sleep", "slender", "slice",
        "slide", "slight", "slim", "slogan", "slot", "slow", "slush", "small",
        "smart", "smile", "smoke", "smooth", "snack", "snake", "snap", "sniff",
        "snow", "soap", "soccer", "social", "sock", "soda", "soft", "solar",
        "soldier", "solid", "solution", "solve", "someone", "song", "soon", "sorry",
        "sort", "soul", "sound", "soup", "source", "south", "space", "spare",
        "spatial", "spawn", "speak", "special", "speed", "spell", "spend", "sphere",
        "spice", "spider", "spike", "spin", "spirit", "split", "spoil", "sponsor",
        "spoon", "sport", "spot", "spray", "spread", "spring", "spy", "square",
        "squeeze", "squirrel", "stable", "stadium", "staff", "stage", "stairs", "stamp",
        "stand", "start", "state", "stay", "steak", "steel", "step", "stereo",
        "stick", "still", "sting", "stock", "stomach", "stone", "stool", "story",
        "stove", "strategy", "street", "strike", "strong", "struggle", "student", "stuff",
        "stumble", "style", "subject", "submit", "subway", "success", "such", "sudden",
        "suffer", "sugar", "suggest", "suit", "sun", "sunny", "sunset", "super",
        "supply", "support", "suppose", "sure", "surface", "surge", "surprise", "surround",
        "survey", "suspect", "sustain", "swallow", "swamp", "swap", "swarm", "swear",
        "sweet", "swift", "swim", "swing", "switch", "sword", "symbol", "symptom",
        "syrup", "system", "table", "tackle", "tag", "tail", "talent", "talk",
        "tank", "tape", "target", "task", "taste", "tattoo", "taxi", "teach",
        "team", "tell", "ten", "tenant", "tennis", "tent", "term", "test",
        "text", "thank", "that", "theme", "then", "theory", "there", "they",
        "thing", "this", "thought", "three", "thrive", "throw", "thumb", "thunder",
        "ticket", "tide", "tiger", "tilt", "timber", "time", "tiny", "tip",
        "tired", "tissue", "title", "toast", "tobacco", "today", "toddler", "toe",
        "together", "toilet", "token", "tomato", "tomorrow", "tone", "tongue", "tonight",
        "tool", "tooth", "top", "topic", "topple", "torch", "tornado", "tortoise",
        "toss", "total", "tourist", "toward", "tower", "town", "toy", "track",
        "trade", "traffic", "tragic", "train", "transfer", "trap", "trash", "travel",
        "tray", "treat", "tree", "trend", "trial", "tribe", "trick", "trigger",
        "trim", "trip", "trophy", "trouble", "truck", "true", "truly", "trumpet",
        "trust", "truth", "try", "tube", "tuition", "tumble", "tuna", "tunnel",
        "turkey", "turn", "turtle", "twelve", "twenty", "twice", "twin", "twist",
        "two", "type", "typical", "ugly", "umbrella", "unable", "unaware", "uncle",
        "uncover", "under", "undo", "unfair", "unfold", "unhappy", "uniform", "unique",
        "unit", "universe", "unknown", "unlock", "until", "unusual", "unveil", "update",
        "upgrade", "uphold", "upon", "upper", "upset", "urban", "urge", "usage",
        "use", "used", "useful", "useless", "usual", "utility", "vacant", "vacuum",
        "vague", "valid", "valley", "valve", "van", "vanish", "vapor", "various",
        "vast", "vault", "vehicle", "velvet", "vendor", "venture", "venue", "verb",
        "verify", "version", "very", "vessel", "veteran", "viable", "vibrant", "vicious",
        "video", "view", "village", "vintage", "violin", "virtual", "virus", "visa",
        "visit", "visual", "vital", "vivid", "vocal", "voice", "void", "volcano",
        "volume", "vote", "voyage", "wage", "wagon", "wait", "walk", "wall",
        "walnut", "want", "warfare", "warm", "warrior", "wash", "wasp", "waste",
        "water", "wave", "way", "wealth", "weapon", "wear", "weasel", "weather",
        "web", "wedding", "weekend", "weird", "welcome", "west", "wet", "whale",
        "what", "wheat", "wheel", "when", "where", "whip", "whisper", "wide",
        "width", "wife", "wild", "will", "win", "window", "wine", "wing",
        "wink", "winner", "winter", "wire", "wisdom", "wise", "wish", "witness",
        "wolf", "woman", "wonder", "wood", "wool", "word", "work", "world",
        "worry", "worth", "wrap", "wreck", "wrestle", "wrist", "write", "wrong",
        "yard", "year", "yellow", "you", "young", "youth", "zebra", "zero",
        "zone", "zoo"
    };

    private string GenerateMnemonic(byte[] seed)
    {
        // DEPLOY-STUB (H1, see conductor/DEPLOY-STEPS-TODO.md): word-index helper
        // for the still-stubbed Solana/Ethereum chains only. Algorand now uses
        // Algorand2's real 25-word mnemonic (see GenerateAlgorandKeypair) and does
        // NOT call this. Replace with a real BIP39 implementation before Solana/
        // Ethereum mnemonics need to be restorable.
        // Simple 12-word mnemonic: use seed bytes to index into wordlist
        var words = new string[12];
        for (int i = 0; i < 12; i++)
        {
            var idx = (seed[i % seed.Length] * 256 + seed[(i + 1) % seed.Length]) % Bip39Wordlist.Length;
            words[i] = Bip39Wordlist[idx];
        }
        return string.Join(' ', words);
    }

    // ─── Base58 encoding ───

    private static readonly char[] Base58Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz".ToCharArray();

    private string Base58Encode(byte[] data)
    {
        int leadingZeros = 0;
        while (leadingZeros < data.Length && data[leadingZeros] == 0)
            leadingZeros++;

        var size = (data.Length - leadingZeros) * 138 / 100 + 1;
        var b58 = new byte[size];
        for (int i = leadingZeros; i < data.Length; i++)
        {
            int carry = data[i];
            for (int j = size - 1; j >= 0; j--)
            {
                carry += 256 * b58[j];
                b58[j] = (byte)(carry % 58);
                carry /= 58;
            }
        }

        int firstNonZero = 0;
        while (firstNonZero < size && b58[firstNonZero] == 0)
            firstNonZero++;

        var result = new char[leadingZeros + (size - firstNonZero)];
        Array.Fill(result, '1', 0, leadingZeros);
        for (int i = 0; i < size - firstNonZero; i++)
            result[leadingZeros + i] = Base58Alphabet[b58[firstNonZero + i]];

        return new string(result);
    }

    // ─── Base32 encoding ───

    private static readonly char[] Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".ToCharArray();

    private string Base32Encode(byte[] data)
    {
        int bits = 0;
        int bitCount = 0;
        var result = new System.Text.StringBuilder();

        for (int i = 0; i < data.Length; i++)
        {
            bits = (bits << 8) | data[i];
            bitCount += 8;
            while (bitCount >= 5)
            {
                bitCount -= 5;
                result.Append(Base32Alphabet[(bits >> bitCount) & 0x1F]);
            }
        }

        if (bitCount > 0)
            result.Append(Base32Alphabet[(bits << (5 - bitCount)) & 0x1F]);

        return result.ToString();
    }
}
