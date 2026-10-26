package dev.keybridge.android

import android.Manifest
import android.content.ClipData
import android.content.ClipboardManager
import android.os.Bundle
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.compose.BackHandler
import androidx.activity.result.contract.ActivityResultContracts
import androidx.biometric.BiometricManager
import androidx.biometric.BiometricPrompt
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.togetherWith
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Image
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.core.content.ContextCompat
import androidx.fragment.app.FragmentActivity
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import dev.keybridge.core.Credential
import dev.keybridge.core.CtapFailure
import dev.keybridge.core.ScreenHistory
import kotlinx.coroutines.delay
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.flow.collectLatest
import androidx.lifecycle.lifecycleScope
import java.text.DateFormat
import java.util.Date

class MainActivity : FragmentActivity() {
    private val runtime get() = (application as KeyBridgeApplication).runtime
    private var biometric: BiometricPrompt? = null
    private var biometricRequest: PhonePrompt? = null
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent { KeyBridgeApp(runtime, ::authenticate) }
        lifecycleScope.launch {
            runtime.broker.prompt.collectLatest { current ->
                if (biometricRequest != null && biometricRequest !== current) {
                    biometric?.cancelAuthentication(); biometric = null; biometricRequest = null
                }
            }
        }
    }
    override fun onStart() { super.onStart(); runtime.foreground = true; runtime.scope.launchRefreshActual(runtime) }
    override fun onStop() { runtime.foreground = false; biometric?.cancelAuthentication(); biometric = null; biometricRequest = null; super.onStop() }
    private fun authenticate(prompt: PhonePrompt) {
        if (!runtime.foreground || runtime.broker.prompt.value !== prompt) return
        if (biometricRequest === prompt && biometric != null) return
        if (BiometricManager.from(this).canAuthenticate(BiometricManager.Authenticators.BIOMETRIC_STRONG) != BiometricManager.BIOMETRIC_SUCCESS) {
            runtime.broker.reject(); runtime.error("Enroll a strong fingerprint in Android Settings before using KeyBridge."); return
        }
        val callback = object : BiometricPrompt.AuthenticationCallback() {
            override fun onAuthenticationSucceeded(result: BiometricPrompt.AuthenticationResult) {
                if (runtime.broker.prompt.value !== prompt) return
                when (prompt) {
                    is PhonePrompt.Biometric -> {
                        if (prompt.signature != null && result.cryptoObject?.signature !== prompt.signature) {
                            prompt.result.completeExceptionally(CtapFailure(0x27)); return
                        }
                        prompt.result.complete(result.cryptoObject?.signature)
                    }
                    is PhonePrompt.Pair -> prompt.result.complete(true)
                    else -> Unit
                }
            }
            override fun onAuthenticationError(errorCode: Int, errString: CharSequence) {
                if (runtime.broker.prompt.value === prompt) runtime.broker.reject()
            }
        }
        val gate = BiometricPrompt(this, ContextCompat.getMainExecutor(this), callback)
        biometric?.cancelAuthentication()
        biometric = gate; biometricRequest = prompt
        val info = BiometricPrompt.PromptInfo.Builder().setTitle("Approve with fingerprint")
            .setSubtitle(prompt.approval.operation).setDescription(prompt.approval.rpId.take(120))
            .setAllowedAuthenticators(BiometricManager.Authenticators.BIOMETRIC_STRONG)
            .setNegativeButtonText("Reject request").setConfirmationRequired(true).build()
        if (prompt is PhonePrompt.Biometric && prompt.signature != null) gate.authenticate(info, BiometricPrompt.CryptoObject(prompt.signature)) else gate.authenticate(info)
    }
}

private val LightScheme = lightColorScheme(
    primary = Color(0xFF315B42), onPrimary = Color.White, primaryContainer = Color(0xFFD7EEDC), onPrimaryContainer = Color(0xFF12321F),
    secondary = Color(0xFF596853), secondaryContainer = Color(0xFFE0E9D8), tertiary = Color(0xFF38656A),
    background = Color(0xFFF7F9F3), surface = Color(0xFFF7F9F3), surfaceVariant = Color(0xFFE8EDE3), onSurface = Color(0xFF1B211B), outline = Color(0xFF758172)
)
private val DarkScheme = darkColorScheme(
    primary = Color(0xFFA0D4AD), onPrimary = Color(0xFF0B381D), primaryContainer = Color(0xFF244F33), onPrimaryContainer = Color(0xFFD3EED9),
    background = Color(0xFF111711), surface = Color(0xFF111711), surfaceVariant = Color(0xFF293328), onSurface = Color(0xFFE0E7DC)
)

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun KeyBridgeApp(runtime: BridgeRuntime, authenticate: (PhonePrompt) -> Unit) {
    val state by runtime.state.collectAsStateWithLifecycle()
    val prompt by runtime.broker.prompt.collectAsStateWithLifecycle()
    var theme by remember { mutableStateOf(runtime.settings.getString("theme", "System")!!) }
    var dynamic by remember { mutableStateOf(runtime.settings.getBoolean("dynamic", true)) }
    var reducedMotion by remember { mutableStateOf(runtime.settings.getBoolean("reducedMotion", false)) }
    val dark = when (theme) { "Dark" -> true; "Light" -> false; else -> isSystemInDarkTheme() }
    val colors = if (dynamic && android.os.Build.VERSION.SDK_INT >= 31) {
        if (dark) dynamicDarkColorScheme(LocalContext.current) else dynamicLightColorScheme(LocalContext.current)
    } else if (dark) DarkScheme else LightScheme
    MaterialTheme(colorScheme = colors, typography = Typography(
        displayMedium = MaterialTheme.typography.displayMedium.copy(fontWeight = FontWeight.Medium, letterSpacing = (-1.4).sp),
        headlineLarge = MaterialTheme.typography.headlineLarge.copy(fontWeight = FontWeight.Medium, letterSpacing = (-0.7).sp)
    )) {
        var history by rememberSaveable { mutableStateOf(arrayListOf(if (runtime.settings.getBoolean("onboarded", false)) "Home" else "Welcome")) }
        val page = history.last()
        var selectedAlias by rememberSaveable { mutableStateOf<String?>(null) }
        val navigate: (String) -> Unit = { history = ArrayList(ScreenHistory.navigate(history, it)) }
        val goBack: () -> Unit = { history = ArrayList(ScreenHistory.back(history)) }
        val main = listOf("Home" to Icons.Outlined.Home, "Vault" to Icons.Outlined.Key, "Activity" to Icons.Outlined.History, "Settings" to Icons.Outlined.Settings)
        val selectedTab = when (page) { "Details" -> "Vault"; "Security", "Computers", "Diagnostics", "About" -> "Settings"; else -> page }
        BackHandler(enabled = prompt == null && page != "Welcome" && (history.size > 1 || page != "Home"), onBack = goBack)
        Scaffold(
            containerColor = MaterialTheme.colorScheme.background,
            topBar = {
                TopAppBar(
                    title = { if (page == "Home" || page == "Welcome") Brand() else Text(page, style = MaterialTheme.typography.titleLarge) },
                    navigationIcon = { if (page !in main.map { it.first } && page != "Welcome") IconButton(onClick = goBack) { Icon(Icons.Outlined.ArrowBack, "Back") } },
                    colors = TopAppBarDefaults.topAppBarColors(containerColor = MaterialTheme.colorScheme.background)
                )
            },
            bottomBar = {
                if (page != "Welcome") NavigationBar(containerColor = MaterialTheme.colorScheme.surface) {
                    main.forEach { (title, icon) -> NavigationBarItem(selected = selectedTab == title,
                        onClick = { navigate(title) }, icon = { Icon(icon, null) }, label = { Text(title) }) }
                }
            }
        ) { inset ->
            Column(Modifier.fillMaxSize().padding(inset)) {
                AnimatedVisibility(state.error != null) {
                    Surface(color = MaterialTheme.colorScheme.errorContainer, shape = RoundedCornerShape(20.dp), modifier = Modifier.padding(horizontal = 20.dp, vertical = 8.dp)) {
                        Row(Modifier.padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
                            Icon(Icons.Outlined.ErrorOutline, null); Spacer(Modifier.width(12.dp))
                            Text(state.error.orEmpty(), Modifier.weight(1f), style = MaterialTheme.typography.bodyMedium)
                            IconButton(onClick = runtime::clearError) { Icon(Icons.Outlined.Close, "Dismiss error") }
                        }
                    }
                }
                AnimatedContent(targetState = page, label = "Screen navigation", modifier = Modifier.weight(1f), transitionSpec = { fadeIn(tween(if (reducedMotion) 0 else 120)) togetherWith fadeOut(tween(if (reducedMotion) 0 else 120)) }) { screen ->
                    when (screen) {
                        "Welcome" -> Onboarding(state, runtime) { runtime.settings.edit().putBoolean("onboarded", true).apply(); history = arrayListOf("Home") }
                        "Home" -> Dashboard(state, runtime, reducedMotion, navigate)
                        "Vault" -> Vault(state) { selectedAlias = it.alias; navigate("Details") }
                        "Details" -> CredentialDetails(state.credentials.firstOrNull { it.alias == selectedAlias }, runtime, goBack)
                        "Activity" -> ActivityScreen(state)
                        "Security" -> SecurityScreen(state, runtime)
                        "Computers" -> ComputersScreen(state, runtime)
                        "Settings" -> SettingsScreen(runtime, theme, dynamic, reducedMotion, { theme = it; runtime.settings.edit().putString("theme", it).apply() },
                            { dynamic = it; runtime.settings.edit().putBoolean("dynamic", it).apply() }, { reducedMotion = it; runtime.settings.edit().putBoolean("reducedMotion", it).apply() }, navigate)
                        "Diagnostics" -> DiagnosticsScreen(runtime)
                        "About" -> AboutScreen()
                        else -> Dashboard(state, runtime, reducedMotion, navigate)
                    }
                }
            }
        }
        if (prompt != null) ApprovalDialog(prompt!!, runtime, reducedMotion, authenticate)
    }
}

@Composable private fun Brand() {
    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        Image(painterResource(R.drawable.ic_keybridge), null, modifier = Modifier.size(32.dp))
        Text("KeyBridge", fontWeight = FontWeight.SemiBold, fontSize = 21.sp, letterSpacing = (-0.6).sp)
    }
}

@Composable private fun PageColumn(content: @Composable ColumnScope.() -> Unit) {
    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.TopCenter) {
        Column(Modifier.widthIn(max = 720.dp).fillMaxSize().verticalScroll(rememberScrollState()).padding(horizontal = 20.dp).padding(top = 12.dp, bottom = 20.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp), content = content)
    }
}
@Composable private fun Eyebrow(text: String) { Text(text.uppercase(), style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.primary, letterSpacing = 1.8.sp) }
@Composable private fun SoftCard(modifier: Modifier = Modifier, content: @Composable ColumnScope.() -> Unit) {
    Surface(modifier = modifier.fillMaxWidth(), color = MaterialTheme.colorScheme.surfaceVariant.copy(alpha = .55f), shape = RoundedCornerShape(20.dp)) {
        Column(Modifier.padding(20.dp), verticalArrangement = Arrangement.spacedBy(14.dp), content = content)
    }
}
@Composable private fun StatusPill(text: String, ready: Boolean) {
    Surface(color = if (ready) MaterialTheme.colorScheme.primaryContainer else MaterialTheme.colorScheme.surfaceVariant, shape = CircleShape) {
        Row(Modifier.padding(horizontal = 14.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(7.dp)) {
            Box(Modifier.size(6.dp).background(if (ready) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.outline, CircleShape))
            Text(text, style = MaterialTheme.typography.labelLarge)
        }
    }
}
@Composable private fun InfoRow(icon: ImageVector, title: String, detail: String, onClick: (() -> Unit)? = null) {
    Row(Modifier.fillMaxWidth().then(if (onClick != null) Modifier.clickable(onClick = onClick) else Modifier).padding(vertical = 7.dp),
        verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(16.dp)) {
        Surface(shape = RoundedCornerShape(16.dp), color = MaterialTheme.colorScheme.primaryContainer.copy(alpha = .65f)) {
            Icon(icon, null, tint = MaterialTheme.colorScheme.primary, modifier = Modifier.padding(12.dp).size(22.dp))
        }
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(3.dp)) {
            Text(title, style = MaterialTheme.typography.titleSmall)
            Text(detail, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        if (onClick != null) Icon(Icons.Outlined.ChevronRight, null, tint = MaterialTheme.colorScheme.outline)
    }
}

@Composable private fun Dashboard(state: PhoneState, runtime: BridgeRuntime, reducedMotion: Boolean, navigate: (String) -> Unit) = PageColumn {
    SoftCard {
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Text("Bridge", Modifier.weight(1f), style = MaterialTheme.typography.titleMedium)
            StatusPill(if (state.paired) "Connected" else if (state.running) "Waiting for PC" else "Stopped", state.paired)
        }
        KeyIllustration(state.paired, reducedMotion, Modifier.fillMaxWidth().height(110.dp))
        Button(onClick = { if (state.running) runtime.stop() else runtime.start() }, modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp)) {
            Icon(if (state.running) Icons.Outlined.StopCircle else Icons.Outlined.Cable, null)
            Spacer(Modifier.width(8.dp)); Text(if (state.running) "Stop bridge" else "Start bridge")
        }
    }
    Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
        Surface(Modifier.weight(1f).heightIn(min = 130.dp).clickable { navigate("Vault") }, shape = RoundedCornerShape(20.dp), color = MaterialTheme.colorScheme.surfaceVariant.copy(alpha = .6f)) {
            Column(Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Icon(Icons.Outlined.Key, null); Text("${state.credentials.size}", style = MaterialTheme.typography.headlineSmall); Text("Passkeys", style = MaterialTheme.typography.labelLarge)
            }
        }
        Surface(Modifier.weight(1f).heightIn(min = 130.dp).clickable { navigate("Security") }, shape = RoundedCornerShape(20.dp), color = MaterialTheme.colorScheme.surfaceVariant.copy(alpha = .6f)) {
            Column(Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Icon(Icons.Outlined.Fingerprint, null); Text(if (state.biometricAvailable) "Ready" else "Set up", style = MaterialTheme.typography.headlineSmall); Text("Fingerprint", style = MaterialTheme.typography.labelLarge)
            }
        }
    }
    SoftCard {
        InfoRow(Icons.Outlined.LaptopWindows, "Computer", state.computer) { navigate("Computers") }
        InfoRow(Icons.Outlined.Shield, "Key storage", state.security)
        InfoRow(Icons.Outlined.Schedule, "Last used", date(state.lastAuthentication))
    }
}
@Composable private fun KeyIllustration(connected: Boolean, reducedMotion: Boolean, modifier: Modifier = Modifier) {
    val transition = rememberInfiniteTransition(label = "Key presence")
    val wave by transition.animateFloat(.3f, 1f, infiniteRepeatable(tween(2200), RepeatMode.Reverse), label = "Cable glow")
    val primary = MaterialTheme.colorScheme.primary; val onPrimary = MaterialTheme.colorScheme.onPrimary; val soft = MaterialTheme.colorScheme.surface
    Canvas(modifier) {
        val cx = size.width / 2; val cy = size.height / 2; val unit = size.height / 170f
        drawCircle(primary.copy(alpha = .06f), 76 * unit, Offset(cx, cy))
        drawCircle(primary.copy(alpha = .09f), 60 * unit, Offset(cx, cy), style = Stroke(1.5f * unit))
        val cable = Path().apply { moveTo(cx - 100 * unit, cy + 26 * unit); cubicTo(cx - 66 * unit, cy + 60 * unit, cx + 28 * unit, cy + 64 * unit, cx + 45 * unit, cy + 32 * unit) }
        drawPath(cable, primary.copy(alpha = if (connected && !reducedMotion) wave else .35f), style = Stroke(7 * unit, cap = StrokeCap.Round))
        drawRoundRect(primary, Offset(cx - 24 * unit, cy - 56 * unit), Size(80 * unit, 115 * unit), CornerRadius(20 * unit))
        drawRoundRect(soft, Offset(cx - 16 * unit, cy - 47 * unit), Size(64 * unit, 91 * unit), CornerRadius(13 * unit))
        drawCircle(primary, 6 * unit, Offset(cx + 16 * unit, cy + 51 * unit))
        drawCircle(primary, 16 * unit, Offset(cx + 16 * unit, cy - 5 * unit), style = Stroke(7 * unit))
        drawLine(primary, Offset(cx + 16 * unit, cy + 10 * unit), Offset(cx + 16 * unit, cy + 28 * unit), 7 * unit, StrokeCap.Round)
        drawLine(primary, Offset(cx + 16 * unit, cy + 21 * unit), Offset(cx + 26 * unit, cy + 21 * unit), 6 * unit, StrokeCap.Round)
        drawRoundRect(primary.copy(alpha = .85f), Offset(cx - 112 * unit, cy - 24 * unit), Size(39 * unit, 53 * unit), CornerRadius(12 * unit))
        drawRoundRect(onPrimary, Offset(cx - 105 * unit, cy - 40 * unit), Size(25 * unit, 22 * unit), CornerRadius(5 * unit), style = Stroke(4 * unit))
    }
}

@Composable private fun Vault(state: PhoneState, onCredential: (Credential) -> Unit) {
    var search by rememberSaveable { mutableStateOf("") }; var discoverable by rememberSaveable { mutableStateOf(false) }; var sortRecent by rememberSaveable { mutableStateOf(true) }
    val filtered = remember(state.credentials, search, discoverable, sortRecent) {
        state.credentials.filter { (!discoverable || it.discoverable) && (it.rpId.contains(search, true) || it.userName.contains(search, true) || it.displayName.contains(search, true)) }
            .let { if (sortRecent) it.sortedByDescending { c -> c.createdAt } else it.sortedBy { c -> c.rpId } }
    }
    Column(Modifier.fillMaxSize().padding(horizontal = 22.dp)) {
        Spacer(Modifier.height(8.dp))
        OutlinedTextField(search, { search = it }, modifier = Modifier.fillMaxWidth(), placeholder = { Text("Search websites and accounts") }, leadingIcon = { Icon(Icons.Outlined.Search, null) }, singleLine = true, shape = RoundedCornerShape(24.dp))
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp), modifier = Modifier.padding(vertical = 12.dp)) {
            FilterChip(discoverable, onClick = { discoverable = !discoverable }, label = { Text("Discoverable") })
            FilterChip(sortRecent, onClick = { sortRecent = !sortRecent }, label = { Text(if (sortRecent) "Newest first" else "Website A–Z") }, leadingIcon = { Icon(Icons.Outlined.Sort, null, Modifier.size(18.dp)) })
        }
        if (filtered.isEmpty()) EmptyState(Icons.Outlined.Key, if (state.credentials.isEmpty()) "Your vault is ready." else "No matching passkeys", if (state.credentials.isEmpty()) "Create a passkey on a website using the Windows virtual security key. It will appear here after you approve." else "Try another website, account, or filter.")
        else LazyColumn(verticalArrangement = Arrangement.spacedBy(10.dp), contentPadding = PaddingValues(bottom = 24.dp)) {
            items(filtered, key = { it.alias }) { credential ->
                Surface(shape = RoundedCornerShape(24.dp), color = MaterialTheme.colorScheme.surfaceVariant.copy(alpha = .5f), modifier = Modifier.fillMaxWidth().clickable { onCredential(credential) }) {
                    Row(Modifier.padding(18.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(14.dp)) {
                        Surface(color = MaterialTheme.colorScheme.primaryContainer, shape = RoundedCornerShape(16.dp)) { Text(credential.rpId.take(1).uppercase(), Modifier.padding(horizontal = 16.dp, vertical = 12.dp), fontSize = 23.sp, fontWeight = FontWeight.Medium) }
                        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(5.dp)) {
                            Text(credential.rpId, style = MaterialTheme.typography.titleMedium, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            Text(credential.displayName.ifEmpty { credential.userName.ifEmpty { "Account handle stored" } }, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                            Text(if (credential.discoverable) "Discoverable · ES256" else "Security key credential · ES256", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.primary)
                        }
                        Icon(Icons.Outlined.ChevronRight, null)
                    }
                }
            }
        }
    }
}

@Composable private fun CredentialDetails(credential: Credential?, runtime: BridgeRuntime, back: () -> Unit) {
    var confirm by remember { mutableStateOf(false) }
    if (credential == null) { EmptyState(Icons.Outlined.Key, "Select a passkey", "Open the vault to choose a credential."); return }
    val current = runtime.state.value.credentials.firstOrNull { it.alias == credential.alias }
    PageColumn {
        Icon(Icons.Outlined.Key, null, Modifier.size(50.dp), tint = MaterialTheme.colorScheme.primary)
        Text(credential.rpId, style = MaterialTheme.typography.headlineLarge)
        Text(credential.displayName.ifEmpty { credential.userName }, style = MaterialTheme.typography.titleMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        if (current == null) { Text("This credential has been deleted."); Button(onClick = back) { Text("Back to vault") } }
        else {
            SoftCard {
                Detail("Type", if (credential.discoverable) "Discoverable passkey" else "Non-discoverable security key")
                Detail("Created", date(credential.createdAt)); Detail("Last used", date(current.lastUsedAt)); Detail("Signature counter", current.counter.toString())
                Detail("Credential ID", summary(credential.id)); Detail("User handle", summary(credential.userId)); Detail("Algorithm", "ES256 / P-256"); Detail("Key storage", credential.security)
            }
            Text("This RP ID was supplied by the WebAuthn client. KeyBridge does not independently verify the browser origin.", style = MaterialTheme.typography.bodySmall)
            OutlinedButton(onClick = { confirm = true }, modifier = Modifier.fillMaxWidth(), colors = ButtonDefaults.outlinedButtonColors(contentColor = MaterialTheme.colorScheme.error)) { Icon(Icons.Outlined.DeleteOutline, null); Spacer(Modifier.width(8.dp)); Text("Delete passkey") }
        }
    }
    if (confirm) AlertDialog(onDismissRequest = { confirm = false }, icon = { Icon(Icons.Outlined.DeleteOutline, null) }, title = { Text("Delete this passkey?") }, text = { Text("${credential.rpId} will no longer accept this credential. Make sure you have another way to sign in. Fingerprint approval is required.") },
        confirmButton = { TextButton(onClick = { confirm = false; runtime.delete(credential) }) { Text("Continue to fingerprint") } }, dismissButton = { TextButton(onClick = { confirm = false }) { Text("Keep passkey") } })
}
@Composable private fun Detail(title: String, value: String) { Column(verticalArrangement = Arrangement.spacedBy(4.dp)) { Text(title, style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant); Text(value, style = MaterialTheme.typography.bodyLarge) } }

@Composable private fun ActivityScreen(state: PhoneState) {
    if (state.history.isEmpty()) { EmptyState(Icons.Outlined.History, "A clear history.", "Your real pairing, registration, authentication and deletion activity will appear here."); return }
    LazyColumn(Modifier.fillMaxSize().padding(horizontal = 22.dp), verticalArrangement = Arrangement.spacedBy(12.dp), contentPadding = PaddingValues(bottom = 24.dp)) {
        item { Text("Recent activity", style = MaterialTheme.typography.titleMedium, modifier = Modifier.padding(vertical = 8.dp)) }
        items(state.history) { entry -> SoftCard { InfoRow(if (entry.operation.contains("successful")) Icons.Outlined.CheckCircle else Icons.Outlined.History, entry.operation, entry.detail); Text(date(entry.at), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant) } }
    }
}

@Composable private fun SecurityScreen(state: PhoneState, runtime: BridgeRuntime) = PageColumn {
    Icon(Icons.Outlined.VerifiedUser, null, Modifier.size(48.dp), tint = MaterialTheme.colorScheme.primary)
    Text("Your security,\nin plain sight.", style = MaterialTheme.typography.headlineLarge)
    SoftCard {
        InfoRow(Icons.Outlined.Fingerprint, "Fresh fingerprint approval", if (state.biometricAvailable) "Strong biometrics available" else "Not ready: enroll a strong biometric in Android Settings")
        InfoRow(Icons.Outlined.Shield, "Keystore security", state.security)
        InfoRow(Icons.Outlined.Key, "Credential key storage", "Non-exportable P-256 keys; never copied to Windows")
        InfoRow(Icons.Outlined.PhonelinkLock, "Device lock", "Credential key use requires an unlocked phone")
        InfoRow(Icons.Outlined.SyncDisabled, "Backup and recovery", "No cloud sync or private-key export")
    }
    Text("If fingerprints change, Android may invalidate existing keys. Register a new credential with each website using another recovery method. Deleting or resetting KeyBridge cannot restore a lost account.", style = MaterialTheme.typography.bodyMedium)
    OutlinedButton(onClick = { runtime.scope.launchRefreshActual(runtime) }, modifier = Modifier.fillMaxWidth()) { Icon(Icons.Outlined.Refresh, null); Spacer(Modifier.width(8.dp)); Text("Refresh security checks") }
}

@Composable private fun ComputersScreen(state: PhoneState, runtime: BridgeRuntime) {
    var revoke by remember { mutableStateOf<PairedComputer?>(null) }
    PageColumn {
        Text("Approved computers", style = MaterialTheme.typography.titleMedium)
        Text("Revoking a computer disconnects it immediately.", color = MaterialTheme.colorScheme.onSurfaceVariant)
        if (state.computers.isEmpty()) EmptyState(Icons.Outlined.LaptopWindows, "No paired computers", "Start the bridge and begin pairing from the Windows companion. Compare both pairing codes before approving.")
        state.computers.forEach { computer -> SoftCard {
            InfoRow(Icons.Outlined.LaptopWindows, computer.name, "Paired ${date(computer.at)}")
            Text(computer.fingerprint.chunked(8).joinToString(" "), fontFamily = FontFamily.Monospace, style = MaterialTheme.typography.bodySmall)
            TextButton(onClick = { revoke = computer }) { Text("Revoke computer", color = MaterialTheme.colorScheme.error) }
        } }
    }
    revoke?.let { computer -> AlertDialog(onDismissRequest = { revoke = null }, title = { Text("Revoke ${computer.name}?") }, text = { Text("It will disconnect immediately and must pair again before it can request authentication.") },
        confirmButton = { TextButton(onClick = { runtime.revoke(computer.fingerprint); revoke = null }) { Text("Revoke") } }, dismissButton = { TextButton(onClick = { revoke = null }) { Text("Cancel") } }) }
}

@Composable private fun SettingsScreen(runtime: BridgeRuntime, theme: String, dynamic: Boolean, reducedMotion: Boolean,
    setTheme: (String) -> Unit, setDynamic: (Boolean) -> Unit, setReducedMotion: (Boolean) -> Unit, navigate: (String) -> Unit) {
    var notifications by remember { mutableStateOf(runtime.settings.getBoolean("notifications", true)) }
    var timeout by remember { mutableStateOf(runtime.settings.getLong("timeout", 90).toFloat()) }
    var reset by remember { mutableStateOf(false) }; var phrase by remember { mutableStateOf("") }
    val requestPermission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        notifications = granted; runtime.settings.edit().putBoolean("notifications", granted).apply()
    }
    PageColumn {
        SoftCard {
            InfoRow(Icons.Outlined.LaptopWindows, "Paired computers", "Review or revoke access") { navigate("Computers") }
            InfoRow(Icons.Outlined.VerifiedUser, "Security", "Keystore and fingerprint checks") { navigate("Security") }
        }
        SoftCard {
            Text("Appearance", style = MaterialTheme.typography.titleMedium)
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) { listOf("System", "Light", "Dark").forEach { choice -> FilterChip(theme == choice, { setTheme(choice) }, label = { Text(choice) }) } }
            SettingSwitch("Dynamic colors", "Use your Android wallpaper palette", dynamic, setDynamic)
            SettingSwitch("Reduced motion", "Keep illustrations and transitions still", reducedMotion, setReducedMotion)
        }
        SoftCard {
            Text("Approval and connection", style = MaterialTheme.typography.titleMedium)
            InfoRow(Icons.Outlined.Fingerprint, "Biometric approval", "Always required for credential operations; cannot be disabled remotely")
            SettingSwitch("Approval notifications", "Invite you to open KeyBridge", notifications) { value ->
                notifications = value; runtime.settings.edit().putBoolean("notifications", value).apply()
                if (value && android.os.Build.VERSION.SDK_INT >= 33) requestPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
            }
            Text("Request timeout · ${timeout.toInt()} seconds", style = MaterialTheme.typography.titleSmall)
            Slider(timeout, { timeout = it }, valueRange = 30f..120f, steps = 2, onValueChangeFinished = { runtime.settings.edit().putLong("timeout", timeout.toLong()).apply() })
        }
        SoftCard {
            Text("Privacy", style = MaterialTheme.typography.titleMedium)
            Text("KeyBridge works locally after setup. It has no cloud backend, analytics or subscription. The vault contains only credentials created here. Diagnostic copies omit RP names, account handles and credential IDs.")
        }
        SoftCard {
            InfoRow(Icons.Outlined.BugReport, "Diagnostics", "Device checks and redacted report") { navigate("Diagnostics") }
            InfoRow(Icons.Outlined.Info, "About", "Version and open-source information") { navigate("About") }
        }
        OutlinedButton(onClick = { phrase = ""; reset = true }, modifier = Modifier.fillMaxWidth(), colors = ButtonDefaults.outlinedButtonColors(contentColor = MaterialTheme.colorScheme.error)) { Text("Reset all KeyBridge credentials") }
    }
    if (reset) AlertDialog(onDismissRequest = { reset = false }, title = { Text("Reset your vault?") }, text = { Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Text("This permanently deletes every KeyBridge private key and paired computer. There is no recovery. Type RESET, then verify your fingerprint.")
        OutlinedTextField(phrase, { phrase = it }, label = { Text("Type RESET") }, singleLine = true)
    } }, confirmButton = { TextButton(enabled = phrase == "RESET", onClick = { reset = false; runtime.reset() }) { Text("Continue to fingerprint") } }, dismissButton = { TextButton(onClick = { reset = false }) { Text("Cancel") } })
}
@Composable private fun SettingSwitch(title: String, detail: String, value: Boolean, changed: (Boolean) -> Unit) {
    Row(Modifier.fillMaxWidth().clickable { changed(!value) }, verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(16.dp)) {
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) { Text(title, style = MaterialTheme.typography.titleSmall); Text(detail, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
        Switch(value, changed)
    }
}

@Composable private fun DiagnosticsScreen(runtime: BridgeRuntime) {
    var copied by remember { mutableStateOf(false) }; val context = LocalContext.current
    PageColumn {
        Text("A transparent\nlook inside.", style = MaterialTheme.typography.headlineLarge)
        SoftCard { Text(runtime.diagnostics(), fontFamily = FontFamily.Monospace, style = MaterialTheme.typography.bodySmall) }
        Button(onClick = { (context.getSystemService(ClipboardManager::class.java)).setPrimaryClip(ClipData.newPlainText("KeyBridge diagnostics", runtime.diagnostics())); copied = true }, modifier = Modifier.fillMaxWidth()) {
            Icon(Icons.Outlined.ContentCopy, null); Spacer(Modifier.width(8.dp)); Text(if (copied) "Redacted diagnostics copied" else "Copy redacted diagnostics")
        }
        OutlinedButton(onClick = { runtime.scope.launchRefreshActual(runtime); copied = false }, modifier = Modifier.fillMaxWidth()) { Text("Refresh checks") }
        Text("The phone cannot inspect the Windows kernel driver or verify Windows HID enumeration. Use the desktop diagnostics for those checks.", style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}
@Composable private fun AboutScreen() {
    val uri = LocalUriHandler.current
    PageColumn {
        Brand()
        Text("Version ${BuildConfig.VERSION_NAME}", style = MaterialTheme.typography.titleMedium)
        Text("Android-backed security key for Windows. Open source under the MIT license.")
        Text("Development release · not FIDO certified", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            OutlinedButton(onClick = { uri.openUri("https://github.com/devz-on/KeyBridge") }, modifier = Modifier.weight(1f)) { Text("GitHub") }
            OutlinedButton(onClick = { uri.openUri("https://github.com/sponsors/devz-on") }, modifier = Modifier.weight(1f)) { Text("Sponsor") }
        }
    }
}
@Composable private fun Onboarding(state: PhoneState, runtime: BridgeRuntime, finish: () -> Unit) {
    var step by rememberSaveable { mutableIntStateOf(0) }
    PageColumn {
        LinearProgressIndicator(progress = { (step + 1) / 4f }, modifier = Modifier.fillMaxWidth())
        Eyebrow("Welcome · ${step + 1} of 4")
        Text(listOf("Your phone as a security key", "Connect your computer", "Set up fingerprint approval", "Finish setup")[step], style = MaterialTheme.typography.headlineMedium)
        KeyIllustration(false, false, Modifier.fillMaxWidth().height(150.dp))
        SoftCard {
            when (step) {
                0 -> { Text("Keep your passkeys on your phone.", style = MaterialTheme.typography.titleLarge); Text("KeyBridge connects your Android Keystore to the Windows FIDO2 platform. Your private authentication keys stay non-exportable on this phone.") }
                1 -> { InfoRow(Icons.Outlined.Cable, "Use a USB data cable", "Enable USB debugging and authorize only your own PC."); InfoRow(Icons.Outlined.LaptopWindows, "Install the Windows companion", "The desktop checks ADB and the separate signed USB/IP driver."); Text("No root, bootloader unlock, cloud account or custom ROM is required.") }
                2 -> { InfoRow(Icons.Outlined.Fingerprint, "Strong biometrics", if (state.biometricAvailable) "Available on this phone" else "Enroll a strong fingerprint in Android Settings"); InfoRow(Icons.Outlined.Shield, "Keystore check", state.security); Text("Every registration and assertion needs fresh approval. Changing fingerprints may invalidate existing credentials.") }
                3 -> { Text("Start the visible bridge, then pair in Windows. Compare the code on both screens before you approve."); Text("Keep this app open to review requests. Android notifications can invite you back; they never approve a request.") }
            }
        }
        Button(onClick = { if (step < 3) step++ else finish() }, modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp)) { Text(if (step < 3) "Continue" else "Open KeyBridge"); Spacer(Modifier.width(8.dp)); Icon(Icons.Outlined.ArrowForward, null) }
        if (step > 0) TextButton(onClick = { step-- }, modifier = Modifier.align(Alignment.CenterHorizontally)) { Text("Back") }
    }
}

@Composable private fun ApprovalDialog(prompt: PhonePrompt, runtime: BridgeRuntime, reducedMotion: Boolean, authenticate: (PhonePrompt) -> Unit) {
    var compared by remember(prompt) { mutableStateOf(false) }; var remaining by remember(prompt) { mutableLongStateOf(runtime.settings.getLong("timeout", 90)) }
    val haptic = LocalHapticFeedback.current
    LaunchedEffect(prompt) { haptic.performHapticFeedback(HapticFeedbackType.LongPress); while (remaining > 0) { delay(1000); remaining-- } }
    Dialog(onDismissRequest = runtime.broker::reject, properties = DialogProperties(usePlatformDefaultWidth = false, dismissOnClickOutside = false)) {
        Surface(modifier = Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
            Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(28.dp).statusBarsPadding().navigationBarsPadding(), verticalArrangement = Arrangement.spacedBy(20.dp)) {
                Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                    Brand(); Spacer(Modifier.weight(1f)); IconButton(onClick = runtime.broker::reject) { Icon(Icons.Outlined.Close, "Reject request") }
                }
                Eyebrow(if (prompt is PhonePrompt.Pair) "Confirm a new connection" else "Your approval is needed")
                Text(prompt.approval.operation, style = MaterialTheme.typography.headlineLarge)
                Icon(if (prompt is PhonePrompt.Pair) Icons.Outlined.PhonelinkLock else Icons.Outlined.Fingerprint, null, Modifier.size(76.dp).align(Alignment.CenterHorizontally), tint = MaterialTheme.colorScheme.primary)
                SoftCard {
                    Detail(if (prompt is PhonePrompt.Pair) "Computer name supplied by the host" else "Relying-party ID supplied by the client", prompt.approval.rpId)
                    if (prompt.approval.account.isNotBlank()) Detail("Account / request", prompt.approval.account)
                    Detail("Connected computer", prompt.approval.computer)
                }
                when (prompt) {
                    is PhonePrompt.Pair -> {
                        Text(prompt.code.chunked(8).chunked(2).joinToString("\n") { it.joinToString("  ") },
                            style = MaterialTheme.typography.bodyLarge.copy(fontFamily = FontFamily.Monospace, fontSize = 16.sp, lineHeight = 24.sp),
                            modifier = Modifier.align(Alignment.CenterHorizontally))
                        Text("Compare ALL 64 characters with the Windows companion. Approve only if every character matches and you started pairing.")
                        Row(Modifier.fillMaxWidth().clickable { compared = !compared }, verticalAlignment = Alignment.CenterVertically) { Checkbox(compared, { compared = it }); Text("All 64 characters match on both screens.", Modifier.weight(1f)) }
                        Button(enabled = compared, onClick = { authenticate(prompt) }, modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp)) { Icon(Icons.Outlined.Fingerprint, null); Spacer(Modifier.width(8.dp)); Text("Pair with fingerprint") }
                    }
                    is PhonePrompt.Account -> {
                        Text("Choose the account you want to use. Fingerprint approval follows.")
                        prompt.credentials.forEach { credential -> OutlinedButton(onClick = { prompt.result.complete(credential) }, modifier = Modifier.fillMaxWidth()) { Text(credential.displayName.ifEmpty { credential.userName.ifEmpty { summary(credential.userId) } }) } }
                    }
                    is PhonePrompt.Biometric -> {
                        Text("The client supplies the RP ID and a client-data hash. Your phone does not independently verify the browser URL or origin.", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        Button(onClick = { authenticate(prompt) }, modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp)) { Icon(Icons.Outlined.Fingerprint, null); Spacer(Modifier.width(8.dp)); Text("Verify fingerprint") }
                    }
                }
                OutlinedButton(onClick = runtime.broker::reject, modifier = Modifier.fillMaxWidth().height(50.dp)) { Text("Reject request") }
                LinearProgressIndicator(progress = { remaining.toFloat() / runtime.settings.getLong("timeout", 90).toFloat() }, modifier = Modifier.fillMaxWidth())
                Text("Expires in $remaining seconds", style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.align(Alignment.CenterHorizontally))
            }
        }
    }
}

@Composable private fun EmptyState(icon: ImageVector, title: String, detail: String) {
    Column(Modifier.fillMaxWidth().padding(vertical = 40.dp, horizontal = 20.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(16.dp)) {
        Surface(color = MaterialTheme.colorScheme.primaryContainer.copy(alpha = .65f), shape = RoundedCornerShape(20.dp)) { Icon(icon, null, Modifier.padding(24.dp).size(42.dp), tint = MaterialTheme.colorScheme.primary) }
        Text(title, style = MaterialTheme.typography.titleLarge)
        Text(detail, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}
private fun date(timestamp: Long): String = if (timestamp == 0L) "Not yet" else DateFormat.getDateTimeInstance(DateFormat.MEDIUM, DateFormat.SHORT).format(Date(timestamp))
private fun summary(bytes: ByteArray): String = if (bytes.isEmpty()) "Not supplied" else bytes.take(8).joinToString("") { "%02x".format(it.toInt() and 255) } + if (bytes.size > 8) "… (${bytes.size} bytes)" else ""

private fun kotlinx.coroutines.CoroutineScope.launchRefreshActual(runtime: BridgeRuntime) {
    launch(Dispatchers.IO) { try { runtime.refresh() } catch (e: Exception) { runtime.error("Security check failed: ${e.javaClass.simpleName}") } }
}
