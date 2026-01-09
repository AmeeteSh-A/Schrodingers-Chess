using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

public partial class SchrodingerChess : Node
{
    [Export]
    public PackedScene TileScene { get; set; }

    [Export]
    public int TileSize { get; set; } = 80;

    public enum PieceType { Pawn, Rook, Knight, Bishop, Queen, King }

    public struct Point
    {
        public int R, C;
        public Point(int r, int c) { R = r; C = c; }
    }

    public class Ghost
    {
        public int Id;
        public float Probability;
    }

    public class Cell
    {
        public List<Ghost> Ghosts = new List<Ghost>();
        public int VisiblePiece = -1;
    }

    public class PieceInfo
    {
        public int Id;
        public PieceType Type;
        public int Team; // 0 mtlb White, 1 matlb Black
    }

    private ENetMultiplayerPeer peer = new ENetMultiplayerPeer();
    private int myTeam = -1; 
    private const int PORT = 9999;
    private const string ADDRESS = "127.0.0.1";

    private int[,] realBoard = new int[8, 8];
    private Cell[,] ghostBoard = new Cell[8, 8];
    private List<PieceInfo> pieceDatabase = new List<PieceInfo>();

    private bool isGameActive = false; 
    private bool viewingRealBoard = false;

    private int currentTurn = 0;
    private int moveCounter = 0;
    private int[] probeTokens = { 0, 0 };

    private Point? selectedSelection = null; 
    private List<Point> currentLegalMoves = new List<Point>(); 

    private List<Point> seekHighlights = new List<Point>(); 
    private int probesUsedThisTurn = 0;

    private List<PieceType> capturedWhite = new List<PieceType>();
    private List<PieceType> capturedBlack = new List<PieceType>();

    private Node2D boardContainer; 
    private Label uiLabel;
    private TextureRect uiBox; 

    private Panel gameOverPanel;
    private Label winnerLabel;
    private Button restartButton;
    private Button viewBoardButton;
    
    private Vector2 BoardOffset = new Vector2(160, 0); 


    public override void _Ready()
    {
        GD.Print("Schrodinger's Chess Multiplayer Engine Ready.");
        GetWindow().Size = new Vector2I(960, 800);

        boardContainer = new Node2D();
        boardContainer.Position = BoardOffset; 
        AddChild(boardContainer); 

        InitUI();
        InitGameOverUI();

        ShowLobby(); 
    }

    private void ShowLobby()
    {
        Control lobby = new Control();
        lobby.Name = "LobbyUI";
        lobby.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(lobby);

        Button btnHost = new Button();
        btnHost.Text = "HOST (Play White)";
        btnHost.Size = new Vector2(200, 50);
        btnHost.Position = new Vector2(380, 300);
        btnHost.Pressed += OnHostPressed;
        lobby.AddChild(btnHost);

        Button btnJoin = new Button();
        btnJoin.Text = "JOIN (Play Black)";
        btnJoin.Size = new Vector2(200, 50);
        btnJoin.Position = new Vector2(380, 370);
        btnJoin.Pressed += OnJoinPressed;
        lobby.AddChild(btnJoin);
    }

    private void OnHostPressed()
    {
        peer.CreateServer(PORT);
        Multiplayer.MultiplayerPeer = peer;
        Multiplayer.PeerConnected += OnPeerConnected;
        myTeam = 0; 
        GetNode("LobbyUI").QueueFree(); 
        UpdateUI("Waiting for player...");
        InitGame(); 
        DrawBoard(); 
    }

    private void OnJoinPressed()
    {
        peer.CreateClient(ADDRESS, PORT);
        Multiplayer.MultiplayerPeer = peer;
        myTeam = 1; 
        GetNode("LobbyUI").QueueFree();
        UpdateUI("Connecting to Host...");
    }

    private void OnPeerConnected(long id)
    {
        if (Multiplayer.IsServer())
        {
            UpdateUI("Player Connected! Game Started.");
            isGameActive = true;
            SyncBoardToClient(); 
        }
    }

    private void SyncBoardToClient()
    {
        List<float> ghostData = new List<float>();
        for(int r=0; r<8; r++) {
            for(int c=0; c<8; c++) {
                foreach(var g in ghostBoard[r,c].Ghosts) {
                    ghostData.Add(r); ghostData.Add(c); ghostData.Add(g.Id); ghostData.Add(g.Probability);
                }
            }
        }

        List<int> visibleData = new List<int>();
        for(int r=0; r<8; r++) {
            for(int c=0; c<8; c++) {
                int pid = -1;
                if (ghostBoard[r,c].VisiblePiece != -1) pid = ghostBoard[r,c].VisiblePiece;
                else if (realBoard[r,c] != -1)
                {
                    PieceInfo p = GetPieceInfo(realBoard[r,c]);
                    if (p.Team == 1) pid = realBoard[r,c];
                }
                
                if (pid != -1) {
                    visibleData.Add(r); visibleData.Add(c); visibleData.Add(pid);
                }
            }
        }

        Rpc(nameof(ReceiveBoardState), ghostData.ToArray(), visibleData.ToArray(), currentTurn, moveCounter, uiLabel.Text);
        DrawBoard();
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer)] 
    private void ReceiveBoardState(float[] ghosts, int[] visibles, int turn, int moves, string msg)
    {
        for(int r=0; r<8; r++) 
            for(int c=0; c<8; c++) {
                ghostBoard[r,c] = new Cell();
                realBoard[r,c] = -1;
            }

        for(int i=0; i<ghosts.Length; i+=4) {
            int r = (int)ghosts[i]; int c = (int)ghosts[i+1]; int id = (int)ghosts[i+2]; float prob = ghosts[i+3];
            ghostBoard[r,c].Ghosts.Add(new Ghost{Id=id, Probability=prob});
        }

        for(int i=0; i<visibles.Length; i+=3) {
            int r = (int)visibles[i]; int c = (int)visibles[i+1]; int id = (int)visibles[i+2];
            realBoard[r,c] = id; 
        }

        currentTurn = turn;
        moveCounter = moves;
        uiLabel.Text = msg; 
        
        if (pieceDatabase.Count == 0) InitGameDataOnly();

        if (Multiplayer.IsServer()) isGameActive = true;
        else isGameActive = true; 

        DrawBoard();
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal =true)]
    private void RequestServerMove(int sr, int sc, int tr, int tc)
    {
        if (!Multiplayer.IsServer()) return; 
        MovePiece(sr, sc, tr, tc);
        SyncBoardToClient();
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal =true)]
    private void RequestServerProbe(int r, int c)
    {
        if (!Multiplayer.IsServer()) return;
        ProbeSquare(r, c);
        SyncBoardToClient();
    }


    private void InitUI()
    {
        int boardHeight = TileSize * 8; 
        int boxHeight = 150;

        uiBox = new TextureRect();
        Texture2D boxTex = GD.Load<Texture2D>("res://assets/dialogueBox.png");
        if (boxTex != null) { uiBox.Texture = boxTex; uiBox.Modulate = Colors.White; }
        else {
            Image img = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
            img.Fill(Colors.White);
            uiBox.Texture = ImageTexture.CreateFromImage(img);
            uiBox.Modulate = new Color(0.1f, 0.1f, 0.1f, 0.9f); 
        }
        uiBox.Position = new Vector2(BoardOffset.X, boardHeight); 
        uiBox.Size = new Vector2(TileSize * 8, boxHeight); 
        uiBox.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize; 
        AddChild(uiBox); 

        uiLabel = new Label();
        uiBox.AddChild(uiLabel);
        LabelSettings settings = new LabelSettings();
        settings.FontSize = 11; 
        uiLabel.LabelSettings = settings;
        uiLabel.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        uiLabel.Position = Vector2.Zero; 
        uiLabel.Size = new Vector2(TileSize * 8, boxHeight);
        uiLabel.HorizontalAlignment = HorizontalAlignment.Center;
        uiLabel.VerticalAlignment = VerticalAlignment.Center; 
        uiLabel.Modulate = new Color("FFE19B"); 
    }

    private void InitGameOverUI()
    {
        gameOverPanel = new Panel();
        gameOverPanel.Size = new Vector2(400, 200);
        gameOverPanel.Position = new Vector2((960 - 400) / 2, (640 - 200) / 2);
        gameOverPanel.Visible = false; 
        AddChild(gameOverPanel);

        winnerLabel = new Label();
        winnerLabel.Size = new Vector2(400, 80);
        winnerLabel.HorizontalAlignment = HorizontalAlignment.Center;
        winnerLabel.VerticalAlignment = VerticalAlignment.Center;
        winnerLabel.Text = "GAME OVER";
        winnerLabel.Modulate = Colors.Green;
        LabelSettings ls = new LabelSettings();
        ls.FontSize = 24;
        winnerLabel.LabelSettings = ls;
        gameOverPanel.AddChild(winnerLabel);

        restartButton = new Button();
        restartButton.Text = "Restart Game";
        restartButton.Size = new Vector2(150, 40);
        restartButton.Position = new Vector2(25, 120);
        restartButton.Pressed += OnRestartClicked;
        gameOverPanel.AddChild(restartButton);

        viewBoardButton = new Button();
        viewBoardButton.Text = "View Real Board";
        viewBoardButton.Size = new Vector2(150, 40);
        viewBoardButton.Position = new Vector2(225, 120);
        viewBoardButton.Pressed += OnViewBoardClicked;
        gameOverPanel.AddChild(viewBoardButton);
    }

    private void UpdateUI(string lastAction)
    {
        string turnName = (currentTurn == 0) ? "WHITE" : "BLACK";
        string info = $"TURN: {turnName}   |   PROBES: W[{probeTokens[0]}]  B[{probeTokens[1]}]\n";
        info += $"----------------------------------\n";
        info += $"{lastAction}";
        if (uiLabel != null) uiLabel.Text = info;
    }

    private void ShowGameOver(string message)
    {
        isGameActive = false;
        gameOverPanel.Visible = true;
        winnerLabel.Text = message;
        gameOverPanel.MoveToFront();
    }

    private void OnRestartClicked()
    {
        if (Multiplayer.IsServer()) {
            InitGame();
            gameOverPanel.Visible = false;
            if (viewingRealBoard) {
                restartButton.GetParent().RemoveChild(restartButton);
                gameOverPanel.AddChild(restartButton);
                restartButton.Position = new Vector2(25, 120);
                viewBoardButton.Visible = true;
                gameOverPanel.Modulate = Colors.White;
                viewingRealBoard = false;
            }
            isGameActive = true;
            SyncBoardToClient();
        }
    }

    private void OnViewBoardClicked()
    {
        viewingRealBoard = true;
        gameOverPanel.Visible = true; 
        viewBoardButton.Visible = false; 
        gameOverPanel.Modulate = new Color(1,1,1,0); 
        restartButton.GetParent().RemoveChild(restartButton);
        AddChild(restartButton); 
        restartButton.Position = new Vector2(BoardOffset.X + (TileSize*8)/2 - 75, 10); 
        DrawBoard();
    }



    private void RegisterPiece(int id, PieceType type, int team)
    {
        pieceDatabase.Add(new PieceInfo { Id = id, Type = type, Team = team });
    }

    private void InitGameDataOnly()
    {
        pieceDatabase.Clear();
        int[] bIDs = { 16, 17, 18, 19, 20, 21, 22, 23 };
        PieceType[] types = { PieceType.Rook, PieceType.Knight, PieceType.Bishop, PieceType.Queen, PieceType.King, PieceType.Bishop, PieceType.Knight, PieceType.Rook };
        for (int i = 0; i < 8; i++) RegisterPiece(bIDs[i], types[i], 1);
        for (int i = 0; i < 8; i++) { int pid = 24 + i; RegisterPiece(pid, PieceType.Pawn, 1); }
        int[] wIDs = { 0, 1, 2, 3, 4, 5, 6, 7 };
        for (int i = 0; i < 8; i++) RegisterPiece(wIDs[i], types[i], 0);
        for (int i = 0; i < 8; i++) { int pid = 8 + i; RegisterPiece(pid, PieceType.Pawn, 0); }
    }

    private void InitGame()
    {
        for (int r = 0; r < 8; r++) {
            for (int c = 0; c < 8; c++) {
                realBoard[r, c] = -1;
                ghostBoard[r, c] = new Cell();
            }
        }
        InitGameDataOnly();
        capturedWhite.Clear();
        capturedBlack.Clear();

        currentTurn = 0; moveCounter = 0; probeTokens[0] = 50; probeTokens[1] = 50;
        isGameActive = false; viewingRealBoard = false;
        
        int[] bIDs = { 16, 17, 18, 19, 20, 21, 22, 23 };
        for (int i = 0; i < 8; i++) { realBoard[0, i] = bIDs[i]; ghostBoard[0, i].VisiblePiece = bIDs[i]; }
        for (int i = 0; i < 8; i++) { int pid = 24 + i; realBoard[1, i] = pid; ghostBoard[1, i].VisiblePiece = pid; }

        int[] wIDs = { 0, 1, 2, 3, 4, 5, 6, 7 };
        for (int i = 0; i < 8; i++) { realBoard[7, i] = wIDs[i]; ghostBoard[7, i].VisiblePiece = wIDs[i]; }
        for (int i = 0; i < 8; i++) { int pid = 8 + i; realBoard[6, i] = pid; ghostBoard[6, i].VisiblePiece = pid; }
        
        UpdateUI("Awaiting Orders...");
        DrawCapturedUI();
    }

    private void HighlightGhosts(int pieceID)
    {
        seekHighlights.Clear();
        for (int r = 0; r < 8; r++) {
            for (int c = 0; c < 8; c++) {
                bool found = (realBoard[r, c] == pieceID);
                if (!found) {
                    foreach(var g in ghostBoard[r, c].Ghosts) {
                        if (g.Id == pieceID) { found = true; break; }
                    }
                }
                if (found) seekHighlights.Add(new Point(r, c));
            }
        }
        DrawBoard();
    }

    private PieceInfo GetPieceInfo(int id)
    {
        foreach (var p in pieceDatabase) if (p.Id == id) return p;
        return new PieceInfo { Id = -1, Type = PieceType.Pawn, Team = -1 };
    }

    private bool IsFrozen(int r, int c) { return ghostBoard[r, c].Ghosts.Count >= 4; }

    
    private bool IsPathClear(int sr, int sc, int tr, int tc, int team) 
    {
        int dr = (tr - sr); int dc = (tc - sc);
        int stepR = (dr == 0) ? 0 : (dr > 0 ? 1 : -1); 
        int stepC = (dc == 0) ? 0 : (dc > 0 ? 1 : -1);
        int currR = sr + stepR; int currC = sc + stepC;


        while (currR != tr || currC != tc) {
            if (realBoard[currR, currC] != -1) return false;
            if (ghostBoard[currR, currC].Ghosts.Count > 0) {
                foreach(var g in ghostBoard[currR, currC].Ghosts) {
                    PieceInfo gInfo = GetPieceInfo(g.Id);
                    if (gInfo.Team != team) return false; 
                    if (gInfo.Team == team && g.Probability > 0.99f) return false; 
                }
            }
            currR += stepR; currC += stepC;
        }
        return true;
    }

    private void ClearAllGhostsOfID(int pieceID)
    {
        for (int r = 0; r < 8; r++)
            for (int c = 0; c < 8; c++)
                ghostBoard[r, c].Ghosts.RemoveAll(g => g.Id == pieceID);
    }

    private void CalculateLegalMoves(int startR, int startC)
    {
        currentLegalMoves.Clear();
        int pieceID = realBoard[startR, startC];
        if (pieceID == -1) return;

        PieceInfo info = GetPieceInfo(pieceID);

        for (int tr = 0; tr < 8; tr++) {
            for (int tc = 0; tc < 8; tc++) {
                if (!IsValidChessMove(startR, startC, tr, tc, info)) continue;
                if (IsFrozen(tr, tc)) continue;
              
                int targetOccupant = realBoard[tr, tc];
                if (targetOccupant != -1 && GetPieceInfo(targetOccupant).Team == info.Team) continue;
                
                if (IsMoveSafeForKing(startR, startC, tr, tc, info.Team))
                    currentLegalMoves.Add(new Point(tr, tc));
            }
        }
    }

    private Point FindKing(int team)
    {
        for (int r = 0; r < 8; r++) {
            for (int c = 0; c < 8; c++) {
                int pid = realBoard[r, c];
                if (pid != -1) {
                    PieceInfo info = GetPieceInfo(pid);
                    if (info.Type == PieceType.King && info.Team == team) return new Point(r, c);
                }
            }
        }
        return new Point(-1, -1);
    }

    private bool IsKingInRealCheck(int team)
    {
        Point kPos = FindKing(team);
        if (kPos.R == -1) return true; 
        return IsSquareAttackedByReal(kPos.R, kPos.C, 1 - team);
    }

    private bool IsMoveSafeForKing(int sr, int sc, int tr, int tc, int team)
    {
        int moverID = realBoard[sr, sc];
        int targetID = realBoard[tr, tc];
        Point kingPos = FindKing(team);
        realBoard[sr, sc] = -1;
        realBoard[tr, tc] = moverID;
        if (kingPos.R == sr && kingPos.C == sc) kingPos = new Point(tr, tc);
        bool isAttacked = IsSquareAttackedByReal(kingPos.R, kingPos.C, 1 - team);
        realBoard[sr, sc] = moverID;
        realBoard[tr, tc] = targetID;
        return !isAttacked; 
    }

    private bool HasAnyLegalRealMoves(int team)
    {
        for (int r = 0; r < 8; r++) {
            for (int c = 0; c < 8; c++) {
                int pid = realBoard[r, c];
                if (pid == -1) continue;
                PieceInfo info = GetPieceInfo(pid);
                if (info.Team != team) continue;
                for (int tr = 0; tr < 8; tr++) {
                    for (int tc = 0; tc < 8; tc++) {
                        if (!IsValidChessMove(r, c, tr, tc, info)) continue;
                        if (IsFrozen(tr, tc)) continue;
                        int targetOcc = realBoard[tr, tc];
                        if (targetOcc != -1 && GetPieceInfo(targetOcc).Team == team) continue;
                        if (IsMoveSafeForKing(r, c, tr, tc, team)) return true;
                    }
                }
            }
        }
        return false;
    }

    private bool IsEnemyOrGhostAt(int r, int c, int myTeam)
    {
        if (r < 0 || r > 7 || c < 0 || c > 7) return false;
        
        if (realBoard[r, c] != -1) {
            if (GetPieceInfo(realBoard[r, c]).Team != myTeam) return true;
        }
        if (ghostBoard[r, c].Ghosts.Count > 0) {
            foreach(var g in ghostBoard[r, c].Ghosts) {
                if (GetPieceInfo(g.Id).Team != myTeam) return true;
            }
        }
        return false;
    }

    private bool IsValidChessMove(int sr, int sc, int tr, int tc, PieceInfo info)
    {
        int dr = tr - sr; int dc = tc - sc;
        int absDr = Math.Abs(dr); int absDc = Math.Abs(dc);
        if (sr == tr && sc == tc) return false;

        switch (info.Type)
        {
            case PieceType.Pawn:
                int forward = (info.Team == 0) ? -1 : 1;
                
                if (dc == 0 && dr == forward) {

                    int targetID = realBoard[tr, tc];
                    if (targetID != -1) {
                        if (ghostBoard[tr, tc].VisiblePiece == targetID) return false;
                        foreach(var g in ghostBoard[tr, tc].Ghosts) if(g.Probability > 0.99f) return false;
                        
                        return true; 
                    }
                    return true;
                }
                
                if (dc == 0 && dr == forward * 2) {
                    int startRow = (info.Team == 0) ? 6 : 1;
                    if (sr != startRow) return false;
                    
                    int midR = sr + forward;
                    
                    if (realBoard[midR, sc] != -1) return false;

                    foreach(var g in ghostBoard[midR, sc].Ghosts) {
                        PieceInfo gInfo = GetPieceInfo(g.Id);
                        if (gInfo.Team != info.Team) return false; 
                        if (gInfo.Team == info.Team && g.Probability > 0.99f) return false;
                    }
                    
                    return true;
                }
                
                if (absDc == 1 && dr == forward) {
                    if (IsEnemyOrGhostAt(tr, tc, info.Team)) return true;
                    return false;
                }
                return false;


            case PieceType.Rook: return (dr == 0 || dc == 0) && IsPathClear(sr, sc, tr, tc, info.Team);
            case PieceType.Bishop: return (absDr == absDc) && IsPathClear(sr, sc, tr, tc, info.Team);
            case PieceType.Queen: return (dr == 0 || dc == 0 || absDr == absDc) && IsPathClear(sr, sc, tr, tc, info.Team);
            case PieceType.Knight: return (absDr == 2 && absDc == 1) || (absDr == 1 && absDc == 2);
            case PieceType.King: return (absDr <= 1 && absDc <= 1);
        }
        return false;
    }

    private List<Point> CalculateGhosts(int startR, int startC, int targetR, int targetC, PieceType type, int team) //#4
    {
        List<Point> validGhosts = new List<Point>();
        int dr = targetR - startR; int dc = targetC - startC;
        if (type == PieceType.Pawn || type == PieceType.King) return validGhosts;

        List<Point> dirs = new List<Point>();
        if (type == PieceType.Rook || type == PieceType.Bishop) {
            dirs.Add(new Point(dr, dc)); dirs.Add(new Point(-dc, dr));
            dirs.Add(new Point(-dr, -dc)); dirs.Add(new Point(dc, -dr));
        }
        else if (type == PieceType.Queen || type == PieceType.Knight) {
            int dist = Math.Max(Math.Abs(dr), Math.Abs(dc));
            if (type == PieceType.Knight) {
                int k1 = Math.Abs(dr), k2 = Math.Abs(dc);
                dirs.Add(new Point(k1, k2)); dirs.Add(new Point(k1, -k2));
                dirs.Add(new Point(-k1, k2)); dirs.Add(new Point(-k1, -k2));
                dirs.Add(new Point(k2, k1)); dirs.Add(new Point(k2, -k1));
                dirs.Add(new Point(-k2, k1)); dirs.Add(new Point(-k2, -k1));
            }
            else {
                dirs.Add(new Point(dist, 0)); dirs.Add(new Point(-dist, 0));
                dirs.Add(new Point(0, dist)); dirs.Add(new Point(0, -dist));
                dirs.Add(new Point(dist, dist)); dirs.Add(new Point(dist, -dist));
                dirs.Add(new Point(-dist, dist)); dirs.Add(new Point(-dist, -dist));
            }
        }

        foreach (var d in dirs) {
            int gr = startR + d.R; int gc = startC + d.C;
            
            if (gr < 0 || gr > 7 || gc < 0 || gc > 7) continue;
            if (IsFrozen(gr, gc)) continue; 
            if (realBoard[gr, gc] != -1) {
                if (gr != targetR || gc != targetC) continue;
            }

            // solid
            if (ghostBoard[gr, gc].VisiblePiece != -1) continue;
            
            // >99 ghost
            bool isCertaintyOccupied = false;
            foreach (var existingGhost in ghostBoard[gr, gc].Ghosts) {
                if (existingGhost.Probability > 0.99f) { isCertaintyOccupied = true; break; }
            }
            if (isCertaintyOccupied) continue;

            if (type != PieceType.Knight) { 
                if (!IsPathClear(startR, startC, gr, gc, team)) continue; 
            }

            bool exists = false;
            foreach (var p in validGhosts) if (p.R == gr && p.C == gc) exists = true;
            if (!exists) validGhosts.Add(new Point(gr, gc));
        }
        return validGhosts;
    }

    private bool IsSquareAttackedByReal(int r, int c, int attackerTeam)
    {
        for (int er = 0; er < 8; er++) {
            for (int ec = 0; ec < 8; ec++) {
                int pid = realBoard[er, ec];
                if (pid != -1) {
                    PieceInfo info = GetPieceInfo(pid);
                    if (info.Team == attackerTeam) {
                        if (IsValidChessMove(er, ec, r, c, info)) return true;
                    }
                }
            }
        }
        return false;
    }

    private float GetProbabilisticThreat(int r, int c, int attackerTeam)
    {
        float threat = 0.0f;
        for (int gr = 0; gr < 8; gr++) {
            for (int gc = 0; gc < 8; gc++) {
                foreach (var g in ghostBoard[gr, gc].Ghosts) {
                    PieceInfo info = GetPieceInfo(g.Id);
                    if (info.Team == attackerTeam) {
                        if (IsValidChessMove(gr, gc, r, c, info)) threat += g.Probability;
                    }
                }
            }
        }
        return threat;
    }

    private void CheckWinCondition()
    {
        Point wKing = FindKing(0);
        Point bKing = FindKing(1);
        if (wKing.R == -1) { ShowGameOver("BLACK WINS! (White King Captured)"); return; }
        if (bKing.R == -1) { ShowGameOver("WHITE WINS! (Black King Captured)"); return; }

        int currentTeam = currentTurn;
        bool inRealCheck = IsKingInRealCheck(currentTeam);
        
        if (inRealCheck) {
            string threatDir = (currentTeam == 0) ? "B->W" : "W->B";
            if (!HasAnyLegalRealMoves(currentTeam)) {
                string winner = (currentTeam == 0) ? "BLACK WINS!" : "WHITE WINS!";
                ShowGameOver($"{winner} (Checkmate {threatDir})");
                return;
            }
            else { UpdateUI($"CHECK! ({threatDir}) Defend your King!"); return; }
        }
        else {
             if (!HasAnyLegalRealMoves(currentTeam)) { ShowGameOver("DRAW (Stalemate)"); return; }
        }

        int enemy = 1 - currentTeam;
        Point kPos = (currentTeam == 0) ? wKing : bKing;
        float probThreat = GetProbabilisticThreat(kPos.R, kPos.C, enemy);
        if (probThreat > 0) UpdateUI($"WARNING: Probabilistic Check! Threat: {(int)(probThreat * 100)}%");
    }

    private void TryPromotePawn(int id, int r)
    {
        foreach (var p in pieceDatabase) {
            if (p.Id == id && p.Type == PieceType.Pawn) {
                if ((p.Team == 0 && r == 0) || (p.Team == 1 && r == 7)) {
                    p.Type = PieceType.Queen;
                    UpdateUI($"PROMOTION! Pawn {id} -> QUEEN");
                }
            }
        }
    }

    private void CapturePiece(int targetID)
    {
        if (targetID == -1) return;
        PieceInfo capturedInfo = GetPieceInfo(targetID);
        if (capturedInfo.Team == 0) capturedWhite.Add(capturedInfo.Type); 
        else capturedBlack.Add(capturedInfo.Type); 
        
        DrawCapturedUI(); 
        for (int r = 0; r < 8; r++)
            for (int c = 0; c < 8; c++)
                if (ghostBoard[r, c].VisiblePiece == targetID) ghostBoard[r, c].VisiblePiece = -1;
        ClearAllGhostsOfID(targetID);
    }

    private void RenormalizeGhosts(int pieceID, float lostMass)
    {
        float remainingMass = 1.0f - lostMass;
        if (remainingMass <= 0.001f) return;
        float scaleFactor = 1.0f / remainingMass;
        for (int r = 0; r < 8; r++)
            for (int c = 0; c < 8; c++)
                foreach (var g in ghostBoard[r, c].Ghosts)
                    if (g.Id == pieceID) g.Probability *= scaleFactor;
    }

    public void MovePiece(int startR, int startC, int targetR, int targetC)
    {
        if (startR < 0 || startR > 7 || startC < 0 || startC > 7) return;
        int pieceID = realBoard[startR, startC];
        if (pieceID == -1) return;

        PieceInfo info = GetPieceInfo(pieceID);
        PieceType originalType = info.Type; 

        if (info.Team != currentTurn) { UpdateUI("Not your turn!"); return; }
        
        GD.Print($"Move: {info.Type} {GetSquareNotation(startR, startC)}->{GetSquareNotation(targetR, targetC)}");

        if (!IsValidChessMove(startR, startC, targetR, targetC, info)) { UpdateUI("Invalid Move Pattern."); return; }
        if (IsFrozen(targetR, targetC)) { UpdateUI("BLOCKED! Tile Frozen by Interference."); return; }

        if (info.Type != PieceType.Knight)
        {
            int dr = targetR - startR;
            int dc = targetC - startC;
            int stepR = (dr == 0) ? 0 : (dr > 0 ? 1 : -1);
            int stepC = (dc == 0) ? 0 : (dc > 0 ? 1 : -1);

            int currR = startR + stepR;
            int currC = startC + stepC;

            while (currR != targetR || currC != targetC)
            {
                Cell cell = ghostBoard[currR, currC];
                if (cell.Ghosts.Count > 0)
                {
                    for (int i = cell.Ghosts.Count - 1; i >= 0; i--)
                    {
                        Ghost g = cell.Ghosts[i];
                        PieceInfo gInfo = GetPieceInfo(g.Id);
                        
                        if (gInfo.Team == info.Team)
                        {
                            float prob = g.Probability;
                            cell.Ghosts.RemoveAt(i);
                            RenormalizeGhosts(g.Id, prob);
                        }
                    }
                }
                currR += stepR;
                currC += stepC;
            }
        }

        int targetOccupant = realBoard[targetR, targetC];
        
        int effectiveTarget = targetOccupant;
        if (effectiveTarget == -1)
        {
            foreach(var g in ghostBoard[targetR, targetC].Ghosts)
            {
                if (g.Probability > 0.99f)
                {
                    effectiveTarget = g.Id;
                    realBoard[targetR, targetC] = g.Id; 
                    ghostBoard[targetR, targetC].VisiblePiece = g.Id; 
                    break;
                }
            }
        }

        bool isPawnForward = (info.Type == PieceType.Pawn && startC == targetC);
        bool isFriendlyFire = (effectiveTarget != -1 && GetPieceInfo(effectiveTarget).Team == info.Team);

        if (effectiveTarget != -1 && (isFriendlyFire || isPawnForward)) {
            GD.Print(">>> HEADBONK!");
            ghostBoard[targetR, targetC].VisiblePiece = effectiveTarget;
            ClearAllGhostsOfID(effectiveTarget); 
            moveCounter++; currentTurn = 1 - currentTurn;
            UpdateUI("HEADBONK! Obstruction hit. Turn Lost.");
            selectedSelection = null; currentLegalMoves.Clear(); DrawBoard(); return; 
        }

        bool isDiagonalPawnMove = (info.Type == PieceType.Pawn && Math.Abs(targetC - startC) == 1);
        
        if (isDiagonalPawnMove && effectiveTarget == -1) {
            GD.Print(">>> FAKE ATTACK!");
            Cell failCell = ghostBoard[targetR, targetC];
            for (int i = failCell.Ghosts.Count - 1; i >= 0; i--) {
                Ghost g = failCell.Ghosts[i];
                float prob = g.Probability;
                failCell.Ghosts.RemoveAt(i);
                RenormalizeGhosts(g.Id, prob);
            }
            moveCounter++; currentTurn = 1 - currentTurn;
            UpdateUI("ATTACK FAILED! Ghost not real. Turn Lost.");
            DrawBoard(); return;
        }

        if (effectiveTarget != -1) CapturePiece(effectiveTarget);

        Cell targetCell = ghostBoard[targetR, targetC];
        for (int i = targetCell.Ghosts.Count - 1; i >= 0; i--) {
            Ghost g = targetCell.Ghosts[i];
            if (g.Id != effectiveTarget) {
                float prob = g.Probability;
                targetCell.Ghosts.RemoveAt(i);
                RenormalizeGhosts(g.Id, prob);
            }
        }

        realBoard[startR, startC] = -1;
        realBoard[targetR, targetC] = pieceID;
        ghostBoard[startR, startC].VisiblePiece = -1;

        TryPromotePawn(pieceID, targetR);
        PieceInfo updatedInfo = GetPieceInfo(pieceID);
        ClearAllGhostsOfID(pieceID); 

        string moveMsg = "";
        bool isSolid = (updatedInfo.Type == PieceType.Pawn || updatedInfo.Type == PieceType.King);
        if (originalType == PieceType.Pawn && updatedInfo.Type == PieceType.Queen) isSolid = true;

        if (isSolid) {
            ghostBoard[targetR, targetC].VisiblePiece = pieceID;
            moveMsg = $"{GetPieceName(updatedInfo.Type, updatedInfo.Team)} to {GetSquareNotation(targetR, targetC)}";
        }
        else {
            List<Point> locations = CalculateGhosts(startR, startC, targetR, targetC, updatedInfo.Type, updatedInfo.Team);
            if (locations.Count == 0) {
                ghostBoard[targetR, targetC].VisiblePiece = pieceID;
                moveMsg = $"{GetPieceName(updatedInfo.Type, updatedInfo.Team)} to {GetSquareNotation(targetR, targetC)} (Confined)";
            }
            else {
                float splitProb = 1.0f / locations.Count;
                foreach (Point p in locations) ghostBoard[p.R, p.C].Ghosts.Add(new Ghost { Id = pieceID, Probability = splitProb });
                moveMsg = $"{GetPieceName(updatedInfo.Type, updatedInfo.Team)} scattered to {locations.Count} locations";
            }
        }
        
        moveCounter++;
        if (moveCounter > 0 && moveCounter % 5 == 0) {
            probeTokens[currentTurn]++;
            if (probeTokens[currentTurn] > 4) probeTokens[currentTurn] = 4;
            moveMsg += " (+1 Probe)";
        }

        currentTurn = 1 - currentTurn;
        probesUsedThisTurn=0;
        seekHighlights.Clear();

        UpdateUI(moveMsg);
        
        selectedSelection = null;
        currentLegalMoves.Clear();
        DrawBoard();
        CheckWinCondition();
    }

    public void ProbeSquare(int r, int c)
    {
        int team = currentTurn;
        
        if (probesUsedThisTurn >= 2) { UpdateUI("Turn Over! You already probed twice."); return; }
        if (probeTokens[team] <= 0) { UpdateUI("Out of Probe Tokens!"); return; }
        if (ghostBoard[r, c].Ghosts.Count == 0) { UpdateUI($"Nothing at {GetSquareNotation(r, c)}"); return; }

        probeTokens[team]--;
        probesUsedThisTurn++; 

        Ghost targetGhost = ghostBoard[r, c].Ghosts[0]; 
        int targetID = targetGhost.Id;
        float prob = targetGhost.Probability;

        string resultMsg = "";

        if (realBoard[r, c] == targetID) 
        {
            ghostBoard[r, c].VisiblePiece = targetID; 
            
            ClearAllGhostsOfID(targetID); 
            
            resultMsg = $"PROBE HIT! Found {GetPieceInfo(targetID).Type} at {GetSquareNotation(r, c)}";

      
            Cell cell = ghostBoard[r, c];
            if (cell.Ghosts.Count > 0)
            {
                for (int i = cell.Ghosts.Count - 1; i >= 0; i--) 
                {
                    Ghost otherG = cell.Ghosts[i];
                    float otherProb = otherG.Probability;
                    
                    cell.Ghosts.RemoveAt(i); 
                    RenormalizeGhosts(otherG.Id, otherProb);
                }
            }
        }
        else 
        {

            ghostBoard[r, c].Ghosts.RemoveAt(0); 
            
            RenormalizeGhosts(targetID, prob);   
            
            resultMsg = $"PROBE MISS! {GetPieceInfo(targetID).Type} not found at {GetSquareNotation(r, c)}.";
        }

        if (probesUsedThisTurn >= 2) {
            currentTurn = 1 - currentTurn;
            probesUsedThisTurn = 0;
            moveCounter++; 
            resultMsg += " (Turn Ended)";
        }

        UpdateUI(resultMsg);
        DrawBoard();
    }



    private void OnTileClicked(int r, int c, InputEvent @event)
    {
        if (!isGameActive) return;
        if (myTeam != currentTurn && myTeam != -1) return; 

        if (@event is InputEventMouseButton mouseEvent && mouseEvent.Pressed)
        {
            if (mouseEvent.ButtonIndex == MouseButton.Left)
            {
                if (selectedSelection == null)
                {
                    int pieceID = realBoard[r, c];
                    if (pieceID != -1) {
                        PieceInfo info = GetPieceInfo(pieceID);
                        if (info.Team == currentTurn) {
                            if (myTeam == -1 || info.Team == myTeam) {
                                selectedSelection = new Point(r, c);
                                CalculateLegalMoves(r, c);
                                DrawBoard(); 
                            }
                        }
                    }
                }
                else
                {
                    Point start = selectedSelection.Value;
                    if (start.R == r && start.C == c) {
                        selectedSelection = null; currentLegalMoves.Clear(); DrawBoard(); 
                    }
                    else {
                        bool isLegal = false;
                        foreach(Point p in currentLegalMoves) if (p.R == r && p.C == c) { isLegal = true; break; }

                        if (isLegal) {
                            RpcId(1, nameof(RequestServerMove), start.R, start.C, r, c);
                            selectedSelection = null; currentLegalMoves.Clear(); DrawBoard();
                        }
                        else {
                            selectedSelection = null; currentLegalMoves.Clear(); DrawBoard();
                        }
                    }
                }
            }
            else if (mouseEvent.ButtonIndex == MouseButton.Right)
            {
                if (myTeam == currentTurn)
                {
                    int pid = realBoard[r, c];
                    if (pid != -1 && GetPieceInfo(pid).Team == myTeam) {
                        HighlightGhosts(pid); return;
                    }

                    if (ghostBoard[r, c].Ghosts.Count > 0) {
                        foreach(var g in ghostBoard[r, c].Ghosts) {
                            if (GetPieceInfo(g.Id).Team == myTeam) {
                                HighlightGhosts(g.Id); return;
                            }
                        }
                    }

                    RpcId(1, nameof(RequestServerProbe), r, c);
                }
            }
        }
    }

    private void DrawBoard()
    {
        foreach (Node child in boardContainer.GetChildren()) child.QueueFree();

        for (int r = 0; r < 8; r++)
        {
            for (int c = 0; c < 8; c++)
            {
                ColorRect tile = new ColorRect();
                tile.Position = new Vector2(c * TileSize, r * TileSize);
                tile.Size = new Vector2(TileSize, TileSize);

                Color baseColor = ((r + c) % 2 == 1) ? new Color(0.4f, 0.4f, 0.4f) : new Color(0.8f, 0.8f, 0.8f);
                
                bool isLegalMove = false;
                foreach (var m in currentLegalMoves) if (m.R == r && m.C == c) isLegalMove = true;

                bool isSeek = false;
                foreach (var s in seekHighlights) if (s.R == r && s.C == c) isSeek = true;
     
                if (isSeek)
                    tile.Color = new Color(1f, 0.8f, 0.2f); 
                else if (selectedSelection != null && selectedSelection.Value.R == r && selectedSelection.Value.C == c)
                    tile.Color = new Color(baseColor.R, baseColor.G + 0.2f, baseColor.B);
                else if (isLegalMove)
                    tile.Color = new Color(baseColor.R, baseColor.G, baseColor.B + 0.3f);
                else
                    tile.Color = baseColor;
                

                if (selectedSelection != null && selectedSelection.Value.R == r && selectedSelection.Value.C == c)
                    tile.Color = new Color(baseColor.R, baseColor.G + 0.2f, baseColor.B);
                else if (isLegalMove)
                    tile.Color = new Color(baseColor.R, baseColor.G, baseColor.B + 0.3f);
                else
                    tile.Color = baseColor;

                int capturedR = r; int capturedC = c;
                if (!viewingRealBoard) tile.GuiInput += (inputEvent) => OnTileClicked(capturedR, capturedC, inputEvent);

                TextureRect templateSprite = new TextureRect();
                templateSprite.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
                templateSprite.Size = new Vector2(TileSize, TileSize);
                templateSprite.Visible = false;
                tile.AddChild(templateSprite);

                Label templateLabel = new Label();
                templateLabel.Visible = false;
                tile.AddChild(templateLabel);

                if (viewingRealBoard)
                {
                    int pid = realBoard[r, c];
                    if (pid != -1) { templateSprite.Visible = true; RenderPiece(templateSprite, pid, 1.0f); }
                }
                else
                {
                    int pid = realBoard[r, c];
                    bool shouldRenderReal = false;

                    if (pid != -1)
                    {
                        if (myTeam == 0) 
                        {
                            int pTeam = GetPieceInfo(pid).Team;
                            if (pTeam == 0 || ghostBoard[r, c].VisiblePiece == pid)
                            {
                                shouldRenderReal = true;
                            }
                        }
                        else 
                        {
                            shouldRenderReal = true;
                        }
                    }

                    if (shouldRenderReal)
                    {
                        templateSprite.Visible = true;
                        RenderPiece(templateSprite, pid, 1.0f);
                    }

                    Cell cell = ghostBoard[r, c];
                    List<Ghost> enemyGhosts = new List<Ghost>();
                    foreach(var g in cell.Ghosts)
                    {
                        int gTeam = GetPieceInfo(g.Id).Team;
                        if (gTeam != myTeam) enemyGhosts.Add(g);
                    }

                    if (enemyGhosts.Count > 0)
                    {
                        int count = enemyGhosts.Count;
                        List<Vector3> layout = new List<Vector3>();
                        if (count == 1) layout.Add(new Vector3(0, 0, 1.0f)); 
                        else if (count == 2) { layout.Add(new Vector3(-0.25f, 0, 0.5f)); layout.Add(new Vector3(0.25f, 0, 0.5f)); }
                        else if (count == 3) { layout.Add(new Vector3(0, -0.25f, 0.5f)); layout.Add(new Vector3(-0.25f, 0.25f, 0.5f)); layout.Add(new Vector3(0.25f, 0.25f, 0.5f)); }
                        else { layout.Add(new Vector3(-0.25f, -0.25f, 0.5f)); layout.Add(new Vector3(0.25f, -0.25f, 0.5f)); layout.Add(new Vector3(-0.25f, 0.25f, 0.5f)); layout.Add(new Vector3(0.25f, 0.25f, 0.5f)); }

                        for (int i = 0; i < count && i < 4; i++)
                        {
                             Ghost g = enemyGhosts[i];
                             Vector3 settings = layout[i];
                             TextureRect ghostSprite = (TextureRect)templateSprite.Duplicate();
                             tile.AddChild(ghostSprite);
                             ghostSprite.Visible = true;
                             ghostSprite.Scale = new Vector2(settings.Z, settings.Z);
                             float offsetX = (TileSize / 2) + (settings.X * TileSize) - ((TileSize * settings.Z) / 2);
                             float offsetY = (TileSize / 2) + (settings.Y * TileSize) - ((TileSize * settings.Z) / 2);
                             ghostSprite.Position = new Vector2(offsetX, offsetY);
                             float alpha = 0.4f + (g.Probability * 0.6f);
                             if (g.Probability > 0.99f) alpha = 1.0f; 
                             RenderPiece(ghostSprite, g.Id, alpha); 
                             if (g.Probability <= 0.99f) {
                                 Label ghostLabel = (Label)templateLabel.Duplicate();
                                 tile.AddChild(ghostLabel);
                                 ghostLabel.Visible = true;
                                 ghostLabel.Text = $"{(int)(g.Probability * 100)}"; 
                                 ghostLabel.Position = new Vector2(offsetX, offsetY + (TileSize * settings.Z * 0.5f)); 
                                 ghostLabel.Scale = new Vector2(0.8f, 0.8f); 
                                 ghostLabel.Modulate = new Color(1, 1, 0); 
                             }
                        }
                        if (cell.Ghosts.Count >= 4) {
                            TextureRect iceOverlay = new TextureRect();
                            string icePath = "res://assets/ice.png";
                            iceOverlay.Texture = GD.Load<Texture2D>(icePath); 
                            iceOverlay.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
                            iceOverlay.Size = new Vector2(TileSize, TileSize);
                            iceOverlay.Position = Vector2.Zero;
                            if (iceOverlay.Texture == null) iceOverlay.Modulate = new Color(0, 1, 1, 0.2f); 
                            else iceOverlay.Modulate = new Color(1, 1, 1, 0.2f); 
                            tile.AddChild(iceOverlay);
                        }
                    }
                }
                boardContainer.AddChild(tile);
            }
        }
    }

    private string GetSquareNotation(int r, int c) {
        char file = (char)('A' + c); int rank = 8 - r; return $"{file}{rank}";
    }
    private string GetPieceName(PieceType type, int team) {
        string prefix = (team == 0) ? "(W)" : "(B)"; return $"{prefix} {type}";
    }
    private TextureRect CreateCapturedSprite(PieceType type, int team) {
        TextureRect sprite = new TextureRect();
        string color = (team == 0) ? "white" : "black";
        string typeName = type.ToString().ToLower();
        string path = $"res://assets/{color}_{typeName}.png";
        sprite.Texture = GD.Load<Texture2D>(path);
        sprite.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
        sprite.Size = new Vector2(TileSize, TileSize);
        sprite.Scale = new Vector2(0.6f, 0.6f);
        sprite.Modulate = new Color(1, 1, 1, 0.9f);
        return sprite;
    }
    private void DrawCapturedUI() {
        foreach (Node child in GetChildren()) if (child.Name.ToString().StartsWith("Graveyard_")) child.QueueFree();
        float startX_W = 20; float startY = 20; float spacingY = 50; float spacingX = 50;
        for (int i = 0; i < capturedWhite.Count; i++) {
            TextureRect sprite = CreateCapturedSprite(capturedWhite[i], 0);
            sprite.Name = "Graveyard_W_" + i;
            int col = i % 2; int row = i / 2;
            sprite.Position = new Vector2(startX_W + (col * spacingX), startY + (row * spacingY));
            AddChild(sprite);
        }
        float startX_B = 800 + 20;
        for (int i = 0; i < capturedBlack.Count; i++) {
            TextureRect sprite = CreateCapturedSprite(capturedBlack[i], 1);
            sprite.Name = "Graveyard_B_" + i;
            int col = i % 2; int row = i / 2;
            sprite.Position = new Vector2(startX_B + (col * spacingX), startY + (row * spacingY));
            AddChild(sprite);
        }
    }
    private void RenderPiece(TextureRect sprite, int pieceID, float opacity) {
        PieceInfo info = GetPieceInfo(pieceID);
        string colorName = (info.Team == 0) ? "white" : "black";
        string typeName = info.Type.ToString().ToLower();
        string path = $"res://assets/{colorName}_{typeName}.png";
        sprite.Texture = GD.Load<Texture2D>(path);
        sprite.Modulate = new Color(1, 1, 1, opacity);
    }
}